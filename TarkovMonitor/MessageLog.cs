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
        internal const int MaxMessages = 200;
        private const string ShortenedMessageSuffix = "\n[Message shortened by Tarkov Monitor.]";
        private readonly object messagesLock = new();
        private readonly List<MonitorMessage> messages = new();
        public event NewLogMessage newMessage = delegate { };

        public IReadOnlyList<MonitorMessage> GetSnapshot()
        {
            lock (messagesLock)
            {
                return messages.ToList();
            }
        }

        public void AddMessage(MonitorMessage message)
        {
            message.Message = LimitMessageLength(message.Message);
            AddMessageCore(message);
        }

        public void AddMessage(string message, string? type = "", string? url = null, string? linkText = null)
        {
            var monMessage = new MonitorMessage(LimitMessageLength(message), type, url, linkText);
            AddMessageCore(monMessage);
        }

        private void AddMessageCore(MonitorMessage message)
        {
            lock (messagesLock)
            {
                messages.Add(message);
                if (messages.Count > MaxMessages)
                {
                    messages.RemoveRange(0, messages.Count - MaxMessages);
                }
            }

            // Notify after releasing the lock so render callbacks can safely take a snapshot.
            newMessage(this, new NewLogMessageArgs(message));
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
