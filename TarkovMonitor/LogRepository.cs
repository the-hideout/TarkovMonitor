using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    // An Event Delegate and Arguments for when a new event is added to the LogRepository
    public delegate void NewLogLine(object source, NewLogLineArgs e);

    public class NewLogLineArgs : EventArgs
    {
        private string type;
        public NewLogLineArgs(string Type)
        {
            type = Type;
        }
    }

    internal class LogRepository
    {
        public event NewLogLine newLog = delegate { };

        public LogRepository()
        {
            Logs = new List<LogLine>();
        }
        public List<LogLine> Logs { get; set; }

        public void AddLog(LogLine message)
        {
            Logs.Add(message);

            // Throw event to let watchers know something has changed
            newLog(this, new NewLogLineArgs(message.Type));
        }

        public void AddLog(string message, string? type = null)
        {
            Logs.Add(new LogLine(message, type));

            // Throw event to let watchers know something has changed
            if (type == null) type = "";
            newLog(this, new NewLogLineArgs(type));
        }
    }
}
