using NAudio.Wave;

namespace TarkovMonitor
{
    /// <summary>
    /// To add new text to speech voices, leverage the site "https://ttsmp3.com/" and use "British English / Brian" for results that match existing voices.
    /// </summary>
    internal class Sound
    {
        public static string AppDataFolder => Application.UserAppDataPath;

		public static string CustomSoundsPath => Path.Join(AppDataFolder, "..", "sounds");
		private static Dictionary<string, bool> customSounds = new();
        public static string SoundPath(string key)
        {
            return Path.Join(CustomSoundsPath, $"{key}.mp3");
        }
        public static void SetCustomSound(string key, string path)
        {
            if (!Directory.Exists(CustomSoundsPath))
            {
                Directory.CreateDirectory(CustomSoundsPath);
            }
            string customPath = SoundPath(key);
            File.Copy(path, customPath);
            customSounds[key] = true;
        }
        public static void RemoveCustomSound(string key)
        {
            if (!customSounds.ContainsKey(key))
            {
                return;
            }
            if (!customSounds[key])
            {
                return;
            }
            File.Delete(SoundPath(key));
            customSounds[key] = false;
        }
        public static bool IsCustom(string key)
        {
            if (!customSounds.ContainsKey(key))
            {
                customSounds[key] = File.Exists(SoundPath(key));
            }
            return customSounds[key];
        }
        public static async Task Play(string key)
        {
            await Task.Run(() => {
                byte[]? resource = null;
                if (IsCustom(key))
                {
                    resource = File.ReadAllBytes(SoundPath(key));
                }
                resource ??= Properties.Resources.ResourceManager.GetObject(key) as byte[];
                if (resource == null)
                {
                    throw new Exception($"Could not load resource for {key}");
                }
                using Stream stream = new MemoryStream(resource);
                using var reader = new Mp3FileReader(stream);
                using var waveOut = new WaveOut();
                waveOut.DeviceNumber = Properties.Settings.Default.notificationsDevice;
                waveOut.Init(reader);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    Thread.Sleep(100);
                }
            });
        }
        public static Dictionary<int, string> GetPlaybackDevices()
        {
            Dictionary<int, string> devices = new()
            {
                { -1, "Default Device" }
            };

            // WaveOutCapabilities.ProductName is backed by the fixed-size
            // WAVEOUTCAPS product-name field, so Windows truncates longer
            // endpoint names before the UI ever receives them. Keep the
            // WaveOut indexes for playback, but use the full Core Audio
            // friendly names for display after matching each endpoint by
            // its original (possibly truncated) WaveOut name.
            List<string> endpointNames = new();
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var endpoints = enumerator.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.DeviceState.Active);
                foreach (var endpoint in endpoints)
                {
                    using (endpoint)
                    {
                        endpointNames.Add(endpoint.FriendlyName);
                    }
                }
            }
            catch
            {
                // The WaveOut names below remain a usable fallback if Core
                // Audio enumeration is unavailable during startup.
            }

            HashSet<int> matchedEndpointIndexes = new();
            for (var deviceNum = 0; deviceNum < WaveOut.DeviceCount; deviceNum++)
            {
                WaveOutCapabilities deviceInfo = WaveOut.GetCapabilities(deviceNum);
                var displayName = deviceInfo.ProductName;
                for (var endpointIndex = 0; endpointIndex < endpointNames.Count; endpointIndex++)
                {
                    if (matchedEndpointIndexes.Contains(endpointIndex))
                    {
                        continue;
                    }

                    var endpointName = endpointNames[endpointIndex];
                    if (!string.Equals(endpointName, deviceInfo.ProductName, StringComparison.OrdinalIgnoreCase)
                        && !endpointName.StartsWith(deviceInfo.ProductName, StringComparison.OrdinalIgnoreCase)
                        && !deviceInfo.ProductName.StartsWith(endpointName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    displayName = endpointName;
                    matchedEndpointIndexes.Add(endpointIndex);
                    break;
                }

                devices.Add(deviceNum, string.IsNullOrWhiteSpace(displayName)
                    ? deviceInfo.ProductName
                    : displayName);
            }
            return devices;
        }
        public enum SoundType
        {
            air_filter_off,
            air_filter_on,
            match_found,
            raid_starting,
            restart_failed_tasks,
            runthrough_over,
            scav_available,
            quest_items,
        }
    }
}
