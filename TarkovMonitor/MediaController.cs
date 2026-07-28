using Windows.Media.Control;

namespace TarkovMonitor
{
    /// <summary>
    /// Controls supported music playback through Windows media sessions.
    /// </summary>
    internal static class MediaController
    {
        private static readonly SemaphoreSlim mediaSessionLock = new(1, 1);
        private static List<GlobalSystemMediaTransportControlsSession> pausedSessions = new();

        private static bool IsSupportedMusicSession(GlobalSystemMediaTransportControlsSession session)
        {
            string source = session.SourceAppUserModelId;
            return source.Contains("spotify", StringComparison.OrdinalIgnoreCase)
                || source.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
                || source.Contains("itunes", StringComparison.OrdinalIgnoreCase);
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
                pausedSessions.Clear();

                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (var session in manager.GetSessions())
                {
                    if (!IsSupportedMusicSession(session)
                        || session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        continue;
                    }

                    if (await session.TryPauseAsync())
                    {
                        pausedSessions.Add(session);
                    }
                }

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
                var sessionsToResume = pausedSessions;
                pausedSessions = new();
                int resumedCount = 0;

                foreach (var session in sessionsToResume)
                {
                    if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    {
                        continue;
                    }

                    if (await session.TryPlayAsync())
                    {
                        resumedCount++;
                    }
                }

                return resumedCount;
            }
            finally
            {
                mediaSessionLock.Release();
            }
        }
    }
}
