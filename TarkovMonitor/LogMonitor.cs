using System.Text;

namespace TarkovMonitor
{
    internal class LogMonitor
    {
        public string Path { get; set; }
        public GameLogType Type { get; set; }
        public event EventHandler InitialReadComplete;
        public event EventHandler<NewLogDataEventArgs> NewLogData;
        public event EventHandler<ExceptionEventArgs> Exception;
        private bool cancel;
        private int MaxBufferLength = 1024;

        public LogMonitor(string path, GameLogType logType)
        {
            Path = path;
            Type = logType;
            cancel = false;
        }

        public async Task Start()
        {
            await Task.Run(() =>
            {
                long fileBytesRead = 0;
                if (Type != GameLogType.Application)
                {
                    try
                    {
                        // if not reading from start, we read new log entries starting after the initial state of the log
                        fileBytesRead = new FileInfo(this.Path).Length;
                        InitialReadComplete?.Invoke(this, new());
                    }
                    catch (Exception ex)
                    {
                        Exception?.Invoke(this, new(ex, $"getting initial {this.Type} log data size"));
                        //System.Diagnostics.Debug.WriteLine($"Error getting initial size of {Path}: {ex.Message}");
                        Thread.Sleep(5000);
                        Start();
                        return;
                    }
                }

                while (true)
                {
                    if (cancel) break;
                    try
                    {
                        var fileSize = new FileInfo(this.Path).Length;
                        if (fileSize > fileBytesRead)
                        {
                            //System.Diagnostics.Debug.WriteLine($"{this.Type} log has {fileSize - fileBytesRead} new bytes");
                            using var fs = new FileStream(this.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fs.Seek(fileBytesRead, SeekOrigin.Begin);
                            var buffer = new byte[MaxBufferLength];
                            var chunks = new List<string>();
                            //System.Diagnostics.Debug.WriteLine($"{this.Type} reading new byte chunk");
                            var bytesRead = fs.Read(buffer, 0, buffer.Length);
                            var newBytesRead = 0;
                            while (bytesRead > 0)
                            {
                                newBytesRead += bytesRead;
                                //System.Diagnostics.Debug.WriteLine($"{this.Type} read {bytesRead} byte chunk");
                                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                                chunks.Add(text);
                                //System.Diagnostics.Debug.WriteLine($"{this.Type} reading new byte chunk (in loop)");
                                bytesRead = fs.Read(buffer, 0, buffer.Length);
                            }
                            //System.Diagnostics.Debug.WriteLine($"{this.Type} log read {newBytesRead} new bytes");
                            NewLogData?.Invoke(this, new NewLogDataEventArgs { Type = this.Type, Data = string.Join("", chunks.ToArray()), InitialRead = fileBytesRead == 0 });
                            if (fileBytesRead == 0)
                            {
                                InitialReadComplete?.Invoke(this, new());
                            }
                            fileBytesRead += newBytesRead;
                        }
                    }
                    catch (Exception ex) {
                        //System.Diagnostics.Debug.WriteLine($"Error reading {this.Type} log: {ex}");
                        Exception?.Invoke(this, new(ex, $"reading {this.Type} log data"));
                    }

                    Thread.Sleep(5000);
                }
            });
        }
        public void Stop()
        {
            cancel = true;
        }
	}
	public class NewLogDataEventArgs : EventArgs
	{
		public GameLogType Type { get; set; }
		public string Data { get; set; }
        public bool InitialRead { get; set; }
	}
}
