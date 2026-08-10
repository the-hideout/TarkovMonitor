using System;
using System.Collections.Generic;
using System.Linq;

namespace TarkovMonitor
{
    // An Event Delegate and Arguments for when a new event is added to the MessageLog
    public delegate void NewLogMessage(object source, NewLogMessageArgs e);

    public class NewLogMessageArgs : EventArgs
    {
        public MonitorMessage Message { get; set; }
        public bool IsRepeat { get; }

        public NewLogMessageArgs(MonitorMessage message, bool isRepeat = false)
        {
            Message = message;
            IsRepeat = isRepeat;
        }
    }

    internal class MessageLog
    {
        private const int MaxMessages = 200;
        private const int MaxRecentDiagnostics = 500;
        private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(30);
        private readonly object gate = new();
        private readonly Dictionary<string, (MonitorMessage Message, DateTime LastSeen)> recentDiagnostics = new(StringComparer.Ordinal);

        public event NewLogMessage newMessage = delegate { };

        public MessageLog(DiagnosticsService diagnostics)
        {
            Diagnostics = diagnostics;
            messages = new List<MonitorMessage>();
        }

        public DiagnosticsService Diagnostics { get; }
        private readonly List<MonitorMessage> messages;
        public IReadOnlyList<MonitorMessage> Messages
        {
            get
            {
                lock (gate)
                {
                    return messages.ToList();
                }
            }
        }
        
        public void AddMessage(MonitorMessage message)
        {
            lock (gate)
            {
                AddToBoundedList(message);
            }

            RaiseMessageAdded(message);
        }

        public void AddMessage(string message, string? type = "", string? url = null)
        {
            AddMessage(new MonitorMessage(message, type, url));
        }

        public DiagnosticSnapshot AddException(
            string displayMessage,
            string code,
            string operation,
            Exception exception,
            string service,
            string stage,
            string? endpoint = null,
            long? durationMilliseconds = null)
        {
            var snapshot = Diagnostics.Capture(
                new DiagnosticContext(code, operation, service, stage, displayMessage, endpoint),
                exception,
                durationMilliseconds);
            AddDiagnostic(snapshot);
            return snapshot;
        }

        public void AddDiagnostic(DiagnosticSnapshot snapshot)
        {
            MonitorMessage? messageToRaise = null;
            var isRepeat = false;
            var now = DateTime.UtcNow;

            lock (gate)
            {
                if (recentDiagnostics.TryGetValue(snapshot.DiagnosticKey, out var previous)
                    && now - previous.LastSeen <= DeduplicationWindow)
                {
                    var occurrenceCount = Math.Max(previous.Message.DiagnosticOccurrenceCount, snapshot.OccurrenceCount);
                    if (snapshot.OccurrenceCount >= previous.Message.DiagnosticOccurrenceCount)
                    {
                        previous.Message.DiagnosticText = snapshot.ClipboardText;
                    }
                    previous.Message.DiagnosticKey = snapshot.DiagnosticKey;
                    previous.Message.DiagnosticOccurrenceCount = occurrenceCount;
                    previous.Message.Message = $"{snapshot.DisplayMessage} (repeated {occurrenceCount} times)";
                    recentDiagnostics[snapshot.DiagnosticKey] = (previous.Message, now);
                    isRepeat = true;
                    messageToRaise = previous.Message;
                }
                else
                {
                    var message = new MonitorMessage(snapshot.DisplayMessage, "exception", diagnosticText: snapshot.ClipboardText)
                    {
                        DiagnosticKey = snapshot.DiagnosticKey,
                        DiagnosticOccurrenceCount = snapshot.OccurrenceCount,
                    };
                    AddToBoundedList(message);
                    recentDiagnostics[snapshot.DiagnosticKey] = (message, now);
                    TrimRecentDiagnostics();
                    messageToRaise = message;
                }
            }

            RaiseMessageAdded(messageToRaise, isRepeat);
        }

        private void AddToBoundedList(MonitorMessage message)
        {
            messages.Add(message);
            while (messages.Count > MaxMessages)
            {
                messages.RemoveAt(0);
            }
        }

        private void TrimRecentDiagnostics()
        {
            while (recentDiagnostics.Count > MaxRecentDiagnostics)
            {
                var oldest = recentDiagnostics.MinBy(entry => entry.Value.LastSeen);
                recentDiagnostics.Remove(oldest.Key);
            }
        }

        private void RaiseMessageAdded(MonitorMessage message, bool isRepeat = false)
        {
            var args = new NewLogMessageArgs(message, isRepeat);
            foreach (NewLogMessage handler in newMessage.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception exception)
                {
                    try
                    {
                        Diagnostics.Capture(
                            new DiagnosticContext(
                                "TM-UI-005",
                                "MessageNotification",
                                "UI",
                                "Notification",
                                "A notification subscriber failed."),
                            exception);
                    }
                    catch
                    {
                        // A notification failure must not prevent the original message from being recorded.
                    }
                }
            }
        }
    }
}
