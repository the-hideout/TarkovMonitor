using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarkovMonitor
{
    // An Event Delegate and Arguments for when a new event is added to the MessageLog
    public delegate void NewLogMessage(object source, NewLogMessageArgs e);

    public class NewLogMessageArgs : EventArgs
    {
        private string type;
        public NewLogMessageArgs(string Type)
        {
            type = Type;
        }
    }

    internal class MessageLog
    {
        public event NewLogMessage newMessage = delegate { };

        public MessageLog()
        {
            Messages = new List<MonitorMessage>();
        }
        public List<MonitorMessage> Messages { get; set; }
        
        public void AddMessage(MonitorMessage message)
        {
            Messages.Add(message);

            // Throw event to let watchers know something has changed
            newMessage(this, new NewLogMessageArgs(message.Type));
        }

        public void AddMessage(string message, string? type = null)
        {
            Messages.Add(new MonitorMessage(message, type));

            // Throw event to let watchers know something has changed
            if (type == null) type = "";
            newMessage(this, new NewLogMessageArgs(type));
        }
    }
}
