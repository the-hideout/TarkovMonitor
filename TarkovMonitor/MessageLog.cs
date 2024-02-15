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
        public MonitorMessage Message { get; set; }
        private string type
        {
            get
            {
                return Message.Type;
            }
        }
        public NewLogMessageArgs(MonitorMessage message)
        {
            Message = message;
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
            newMessage(this, new NewLogMessageArgs(message));
        }

        public void AddMessage(string message, string? type = "", string? url = null)
        {
            var monMessage = new MonitorMessage(message, type, url);
            Messages.Add(monMessage);

            // Throw event to let watchers know something has changed
            newMessage(this, new NewLogMessageArgs(monMessage));
        }
    }
}
