using NAudio.Wave;

namespace TarkovMonitor
{
    internal class Sound
    {
        public static Task Play(string key)
        {
            byte[] resource = null;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var filepath = Path.Combine(localAppData, "TarkovMonitor", "sounds", $"{key}.mp3");
            if (File.Exists(filepath))
            {
                resource = File.ReadAllBytes(filepath);
            }
            resource ??= Properties.Resources.ResourceManager.GetObject(key) as byte[];
            Stream stream = new MemoryStream(resource);
            var reader = new Mp3FileReader(stream);
            var waveOut = new WaveOut();
            waveOut.Init(reader);
            var tcs = new TaskCompletionSource();
            waveOut.PlaybackStopped += (object? sender, StoppedEventArgs e) => {
                waveOut.Dispose();
                reader.Dispose();
                stream.Dispose();
                tcs.SetResult();
            };
            waveOut.Play();
            return tcs.Task;
        }
    }
}
