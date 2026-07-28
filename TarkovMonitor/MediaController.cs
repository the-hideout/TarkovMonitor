using NAudio.CoreAudioApi;
using Windows.Media;
using Windows.Media.Control;

namespace TarkovMonitor
{
    /// <summary>
    /// Controls supported music playback through Windows media sessions.
    /// </summary>
    internal static class MediaController
    {
        private const int FadeDurationMilliseconds = 3000;
        private const int FadeFrameMilliseconds = 16;
        private const int ResumeDelayMilliseconds = 2000;
        private const int PauseTransitionTimeoutMilliseconds = 1000;
        private const int SilenceTransitionTimeoutMilliseconds = 1000;
        private const int SilentSampleCount = 3;
        private const float SilencePeakThreshold = 0.001f;
        private static readonly SemaphoreSlim mediaSessionLock = new(1, 1);
        private static readonly string[] browserSources = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc" };
        private static List<PausedMediaSession> pausedSessions = new();
        private static List<AudioSessionVolume> pausedSessionVolumes = new();

        private sealed record PausedMediaSession(GlobalSystemMediaTransportControlsSession Session, string SourceId);
        private sealed record AudioSessionVolume(string DeviceId, uint ProcessId, string SourceId, float Volume);
        private sealed record ActiveAudioSession(AudioSessionControl Session, SimpleAudioVolume VolumeControl, float OriginalVolume);

        private static bool IsBrowserSource(string source)
        {
            return browserSources.Any(browser => source.Contains(browser, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string?> GetMusicSourceIdAsync(GlobalSystemMediaTransportControlsSession session)
        {
            string sourceId = session.SourceAppUserModelId;
            if (IsBrowserSource(sourceId)) return null;

            MediaPlaybackType? playbackType = session.GetPlaybackInfo().PlaybackType;
            if (playbackType == MediaPlaybackType.Music) return sourceId;
            if (playbackType == MediaPlaybackType.Video || playbackType == MediaPlaybackType.Image) return null;

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties.PlaybackType == MediaPlaybackType.Music) return sourceId;
            if (properties.PlaybackType == MediaPlaybackType.Video || properties.PlaybackType == MediaPlaybackType.Image) return null;

            // Some desktop players omit PlaybackType but provide music-specific metadata.
            return !string.IsNullOrWhiteSpace(properties.Artist)
                || !string.IsNullOrWhiteSpace(properties.AlbumArtist)
                || !string.IsNullOrWhiteSpace(properties.AlbumTitle)
                || properties.Genres.Count > 0
                    ? sourceId
                    : null;
        }

        private static string NormalizeIdentity(string value)
        {
            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static bool AudioProcessMatchesSource(string processName, string sourceId)
        {
            string processIdentity = NormalizeIdentity(processName);
            string sourceIdentity = NormalizeIdentity(sourceId);
            return processIdentity.Length >= 4 && sourceIdentity.Contains(processIdentity);
        }

        private static List<AudioSessionVolume> GetAudioSessionVolumes(IReadOnlyCollection<string> sourceIds)
        {
            List<AudioSessionVolume> volumes = new();
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.IsSystemSoundsSession
                            || session.GetProcessID == 0
                            || session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                        {
                            continue;
                        }

                        try
                        {
                            using var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID);
                            string? sourceId = sourceIds.FirstOrDefault(source => AudioProcessMatchesSource(process.ProcessName, source));
                            if (sourceId != null)
                            {
                                volumes.Add(new(device.ID, session.GetProcessID, sourceId, session.SimpleAudioVolume.Volume));
                            }
                        }
                        catch (ArgumentException)
                        {
                            // The application exited while its audio session was being inspected.
                        }
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }

            return volumes.GroupBy(volume => (volume.DeviceId, volume.ProcessId)).Select(group => group.First()).ToList();
        }

        private static void SetAudioSessionVolumes(IEnumerable<AudioSessionVolume> volumes, float progress)
        {
            var targetVolumes = volumes.ToDictionary(volume => (volume.DeviceId, volume.ProcessId));
            if (targetVolumes.Count == 0) return;

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (targetVolumes.TryGetValue((device.ID, session.GetProcessID), out var target))
                        {
                            session.SimpleAudioVolume.Volume = target.Volume * progress;
                        }
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }
        }

        private static async Task FadeAudioSessionsAsync(IEnumerable<AudioSessionVolume> volumes, bool fadeIn)
        {
            var volumeList = volumes.ToList();
            var targetVolumes = volumeList.ToDictionary(volume => (volume.DeviceId, volume.ProcessId));
            if (targetVolumes.Count == 0) return;

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            List<ActiveAudioSession> activeSessions = new();

            try
            {
                foreach (var device in devices)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (targetVolumes.TryGetValue((device.ID, session.GetProcessID), out var target))
                        {
                            activeSessions.Add(new(session, session.SimpleAudioVolume, target.Volume));
                        }
                        else
                        {
                            session.Dispose();
                        }
                    }
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < FadeDurationMilliseconds)
                {
                    float progress = (float)stopwatch.ElapsedMilliseconds / FadeDurationMilliseconds;
                    float easedProgress = progress * progress * (3 - 2 * progress);
                    float volumeProgress = fadeIn ? easedProgress : 1 - easedProgress;

                    foreach (var audioSession in activeSessions)
                    {
                        audioSession.VolumeControl.Volume = audioSession.OriginalVolume * volumeProgress;
                    }

                    await Task.Delay(FadeFrameMilliseconds);
                }

                foreach (var audioSession in activeSessions)
                {
                    audioSession.VolumeControl.Volume = fadeIn ? audioSession.OriginalVolume : 0;
                }
            }
            finally
            {
                foreach (var audioSession in activeSessions)
                {
                    audioSession.VolumeControl.Dispose();
                    audioSession.Session.Dispose();
                }

                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }

        private static async Task HoldMutedUntilPausedAsync(
            IReadOnlyCollection<PausedMediaSession> requestedSessions,
            IReadOnlyCollection<AudioSessionVolume> volumes)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            do
            {
                // A successful TryPauseAsync means the command was accepted, but the
                // player may still emit audio until its playback state changes.
                SetAudioSessionVolumes(volumes, 0);
                int confirmedPausedCount = requestedSessions
                    .Where(session => session.Session.GetPlaybackInfo().PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    .Count();

                if (confirmedPausedCount == requestedSessions.Count)
                {
                    break;
                }

                await Task.Delay(FadeFrameMilliseconds);
            }
            while (stopwatch.ElapsedMilliseconds < PauseTransitionTimeoutMilliseconds);

            await HoldMutedUntilSilentAsync(volumes);

            // Pin the sessions at zero once more before their original volume is
            // restored while paused.
            SetAudioSessionVolumes(volumes, 0);
        }

        private static async Task HoldMutedUntilSilentAsync(IReadOnlyCollection<AudioSessionVolume> volumes)
        {
            var targets = volumes.ToDictionary(volume => (volume.DeviceId, volume.ProcessId));
            if (targets.Count == 0) return;

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            List<ActiveAudioSession> sessions = new();

            try
            {
                foreach (var device in devices)
                {
                    var deviceSessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < deviceSessions.Count; i++)
                    {
                        var session = deviceSessions[i];
                        if (targets.TryGetValue((device.ID, session.GetProcessID), out var target))
                        {
                            sessions.Add(new(session, session.SimpleAudioVolume, target.Volume));
                        }
                        else
                        {
                            session.Dispose();
                        }
                    }
                }

                int consecutiveSilentSamples = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < SilenceTransitionTimeoutMilliseconds)
                {
                    bool silent = true;
                    foreach (var session in sessions)
                    {
                        session.VolumeControl.Volume = 0;
                        if (session.Session.AudioMeterInformation.MasterPeakValue > SilencePeakThreshold)
                        {
                            silent = false;
                        }
                    }

                    consecutiveSilentSamples = silent ? consecutiveSilentSamples + 1 : 0;
                    if (consecutiveSilentSamples >= SilentSampleCount)
                    {
                        break;
                    }

                    await Task.Delay(FadeFrameMilliseconds);
                }
            }
            finally
            {
                foreach (var session in sessions)
                {
                    session.VolumeControl.Dispose();
                    session.Session.Dispose();
                }

                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }

        /// <summary>
        /// Pauses supported music sessions that are currently playing and remembers
        /// only the sessions successfully paused by TarkovMonitor.
        /// </summary>
        public static async Task<int> PauseAsync()
        {
            await mediaSessionLock.WaitAsync();
            try
            {
                // A duplicate raid-start event must not erase sessions already
                // paused and tracked for the current raid.
                if (pausedSessions.Count > 0) return 0;

                pausedSessions.Clear();
                pausedSessionVolumes.Clear();

                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                List<PausedMediaSession> playingSessions = new();
                foreach (var session in manager.GetSessions())
                {
                    if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        continue;
                    }

                    string? sourceId = await GetMusicSourceIdAsync(session);
                    if (sourceId != null)
                    {
                        playingSessions.Add(new(session, sourceId));
                    }
                }

                if (playingSessions.Count == 0) return 0;

                var sourceIds = playingSessions.Select(session => session.SourceId).ToHashSet();
                var originalVolumes = GetAudioSessionVolumes(sourceIds);
                try
                {
                    await FadeAudioSessionsAsync(originalVolumes, fadeIn: false);

                    List<PausedMediaSession> pauseRequestsAccepted = new();
                    foreach (var mediaSession in playingSessions)
                    {
                        if (await mediaSession.Session.TryPauseAsync())
                        {
                            pauseRequestsAccepted.Add(mediaSession);
                        }
                    }

                    // Retain accepted pause requests even if a player reports its
                    // state transition later than the handoff timeout.
                    pausedSessions = pauseRequestsAccepted;
                    await HoldMutedUntilPausedAsync(pauseRequestsAccepted, originalVolumes);
                }
                finally
                {
                    SetAudioSessionVolumes(originalVolumes, 1);
                }

                var pausedSources = pausedSessions.Select(session => session.SourceId).ToHashSet();
                pausedSessionVolumes = originalVolumes.Where(volume => pausedSources.Contains(volume.SourceId)).ToList();

                return pausedSessions.Count;
            }
            finally
            {
                mediaSessionLock.Release();
            }
        }

        /// <summary>
        /// Resumes only the music sessions that TarkovMonitor paused at raid start.
        /// </summary>
        public static async Task<int> ResumeAsync()
        {
            await mediaSessionLock.WaitAsync();
            try
            {
                var sessionsToResume = pausedSessions.ToList();
                var volumesToRestore = pausedSessionVolumes.ToList();
                if (sessionsToResume.Count == 0) return 0;

                await Task.Delay(ResumeDelayMilliseconds);

                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var currentSessions = manager.GetSessions();
                int resumedCount = 0;
                var trackedSources = sessionsToResume.Select(session => session.SourceId).ToHashSet();
                var volumesToFade = volumesToRestore.Where(volume => trackedSources.Contains(volume.SourceId)).ToList();

                HashSet<string> resumedApps = new();
                List<PausedMediaSession> resolvedSessions = new();
                try
                {
                    SetAudioSessionVolumes(volumesToFade, 0);

                    foreach (var trackedSession in sessionsToResume)
                    {
                        var currentSession = currentSessions.FirstOrDefault(session =>
                            string.Equals(session.SourceAppUserModelId, trackedSession.SourceId, StringComparison.OrdinalIgnoreCase));
                        var mediaSession = currentSession ?? trackedSession.Session;
                        var status = mediaSession.GetPlaybackInfo().PlaybackStatus;

                        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            resolvedSessions.Add(trackedSession);
                            continue;
                        }

                        if (await mediaSession.TryPlayAsync())
                        {
                            resumedCount++;
                            resumedApps.Add(trackedSession.SourceId);
                            resolvedSessions.Add(trackedSession);
                        }
                    }

                    var resumedVolumes = volumesToFade.Where(volume => resumedApps.Contains(volume.SourceId)).ToList();
                    await FadeAudioSessionsAsync(resumedVolumes, fadeIn: true);
                }
                finally
                {
                    SetAudioSessionVolumes(volumesToFade, 1);
                }

                // Keep unsuccessful sessions so RaidEnded/RaidExited can retry.
                pausedSessions.RemoveAll(session => resolvedSessions.Contains(session));
                var unresolvedSources = pausedSessions.Select(session => session.SourceId).ToHashSet();
                pausedSessionVolumes = pausedSessionVolumes
                    .Where(volume => unresolvedSources.Contains(volume.SourceId))
                    .ToList();

                return resumedCount;
            }
            finally
            {
                mediaSessionLock.Release();
            }
        }
    }
}
