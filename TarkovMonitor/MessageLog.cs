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
        internal const int MaxMessageLength = 2048;
        private const string ShortenedMessageSuffix = "\n[Message shortened by Tarkov Monitor.]";
        public event NewLogMessage newMessage = delegate { };

        public MessageLog()
        {
            Messages = new List<MonitorMessage>();
        }
        public List<MonitorMessage> Messages { get; set; }
        
        public void AddMessage(MonitorMessage message)
        {
            message.Message = LimitMessageLength(message.Message);
            Messages.Add(message);

            // Throw event to let watchers know something has changed
            newMessage(this, new NewLogMessageArgs(message));
        }

        public void AddMessage(string message, string? type = "", string? url = null, string? linkText = null)
        {
            var monMessage = new MonitorMessage(LimitMessageLength(message), type, url, linkText);
            Messages.Add(monMessage);

            // Throw event to let watchers know something has changed
            newMessage(this, new NewLogMessageArgs(monMessage));
        }

        private static string LimitMessageLength(string message)
        {
            if (message.Length <= MaxMessageLength)
            {
                return message;
            }

            return message[..(MaxMessageLength - ShortenedMessageSuffix.Length)] + ShortenedMessageSuffix;
        }
    }
}
