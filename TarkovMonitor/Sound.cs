using NAudio.Wave;

namespace TarkovMonitor
{
    internal class Sound
    {
        public static string CustomSoundsPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMonitor", "sounds");
        private static Dictionary<string, bool> customSounds = new();
        public static string SoundPath(string key)
        {
            return Path.Combine(CustomSoundsPath, $"{key}.mp3");
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
                byte[] resource = null;
                if (IsCustom(key))
                {
                    resource = File.ReadAllBytes(SoundPath(key));
                }
                resource ??= Properties.Resources.ResourceManager.GetObject(key) as byte[];
                using Stream stream = new MemoryStream(resource);
                using var reader = new Mp3FileReader(stream);
                using var waveOut = new WaveOut();
                waveOut.Init(reader);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    Thread.Sleep(100);
                }
            });
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
        }
    }
}
