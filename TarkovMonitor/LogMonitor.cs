using System.Text;

namespace TarkovMonitor
{
    internal class LogMonitor
    {
        public string Path { get; set; }
        public GameLogType Type { get; set; }
        public event EventHandler? InitialReadComplete;
        public event EventHandler<NewLogDataEventArgs>? NewLogData;
        public event EventHandler<ExceptionEventArgs>? Exception;
        private volatile bool cancel;
        private const int MaxBufferLength = 1024;

        public LogMonitor(string path, GameLogType logType)
        {
            Path = path;
            Type = logType;
            cancel = false;
        }

        public async Task Start()
        {
            await Task.Run(async () =>
            {
                long fileBytesRead = 0;
                var initialReadReported = false;
                var readFailureReported = false;
                if (Type != GameLogType.Application)
                {
                    try
                    {
                        // Non-application logs start at their current end. Only new
                        // records should be processed after Tarkov Monitor launches.
                        fileBytesRead = new FileInfo(Path).Length;
                    }
                    catch (Exception ex)
                    {
                        // A log can rotate between directory enumeration and this read.
                        // Do not hold the entire initial-read boundary open; the normal
                        // polling loop will reconnect if the file reappears.
                        Exception?.Invoke(this, new(ex, $"getting initial {Type} log data size"));
                        readFailureReported = true;
                    }
                    InitialReadComplete?.Invoke(this, EventArgs.Empty);
                    initialReadReported = true;
                }

                while (!cancel)
                {
                    try
                    {
                        var fileSize = new FileInfo(Path).Length;
                        if (fileSize < fileBytesRead)
                        {
                            // EFT can truncate a log in place while retaining its name.
                            fileBytesRead = 0;
                        }
                        if (fileSize > fileBytesRead)
                        {
                            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fs.Seek(fileBytesRead, SeekOrigin.Begin);
                            var buffer = new byte[MaxBufferLength];
                            var chunks = new List<string>();
                            var bytesRead = fs.Read(buffer, 0, buffer.Length);
                            var newBytesRead = 0;
                            while (bytesRead > 0)
                            {
                                newBytesRead += bytesRead;
                                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                                chunks.Add(text);
                                bytesRead = fs.Read(buffer, 0, buffer.Length);
                            }
                            NewLogData?.Invoke(this, new NewLogDataEventArgs
                            {
                                Type = Type,
                                Data = string.Join("", chunks),
                                InitialRead = !initialReadReported,
                            });
                            if (!initialReadReported)
                            {
                                InitialReadComplete?.Invoke(this, EventArgs.Empty);
                                initialReadReported = true;
                            }
                            fileBytesRead += newBytesRead;
                        }
                        else if (Type == GameLogType.Application && !initialReadReported)
                        {
                            // An empty application log still represents a completed initial
                            // read. New entries will continue to be processed when appended.
                            InitialReadComplete?.Invoke(this, EventArgs.Empty);
                            initialReadReported = true;
                        }
                        readFailureReported = false;
                    }
                    catch (Exception ex) {
                        if (!readFailureReported)
                        {
                            Exception?.Invoke(this, new(ex, $"reading {Type} log data"));
                            readFailureReported = true;
                        }
                    }

                    await Task.Delay(Type == GameLogType.Output
                        ? TimeSpan.FromMilliseconds(250)
                        : TimeSpan.FromSeconds(5));
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
