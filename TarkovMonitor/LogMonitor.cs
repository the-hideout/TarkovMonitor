using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    internal class LogMonitor
    {
        public string Path { get; set; }
        public GameWatcher.LogType Type { get; set; }
        public event EventHandler<NewLogDataEventArgs> NewLogData;
        private bool cancel;

        public LogMonitor(string path, GameWatcher.LogType logType)
        {
            Path = path;
            Type = logType;
            cancel = false;
        }

        public async Task Start()
        {
            await Task.Run(() =>
            {
                var initialFileSize = new FileInfo(this.Path).Length;
                var lastReadLength = 0;
                var initialRead = false;

                while (true)
                {
                    if (cancel) break;
                    try
                    {
                        var fileSize = new FileInfo(this.Path).Length;
                        if (fileSize > lastReadLength)
                        {
                            using (var fs = new FileStream(this.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                fs.Seek(lastReadLength, SeekOrigin.Begin);
                                var buffer = new byte[1024];
                                var lines = new List<string>();
                                while (true)
                                {
                                    var bytesRead = fs.Read(buffer, 0, buffer.Length);
                                    lastReadLength += bytesRead;

                                    if (bytesRead == 0)
                                        break;

                                    var text = ASCIIEncoding.ASCII.GetString(buffer, 0, bytesRead);

                                    lines.Add(text);
                                }
                                if (initialRead)
                                {
                                    NewLogData?.Invoke(this, new NewLogDataEventArgs { Type = this.Type, Data = String.Join("", lines.ToArray()) });
                                }
                                initialRead = true;
                            }
                        }
                    }
                    catch { }

                    Thread.Sleep(5000);
                }
            });
        }
        public void Stop()
        {
            cancel = true;
        }

        public class NewLogDataEventArgs : EventArgs
        {
            public GameWatcher.LogType Type { get; set; }
            public string Data { get; set; }
        }
    }
}
