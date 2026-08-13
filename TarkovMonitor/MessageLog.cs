using System;
using System.Collections.Generic;
using System.Linq;

namespace TarkovMonitor
{
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
        internal const int MaxMessageLength = 2048;
        internal const int MaxMessages = 200;
        private const string ShortenedMessageSuffix = "\n[Message shortened by Tarkov Monitor.]";
        private const int MaxRecentDiagnostics = 500;
        private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(30);
        private readonly object gate = new();
        private readonly Dictionary<string, (MonitorMessage Message, DateTime LastSeen)> recentDiagnostics = new(StringComparer.Ordinal);
        private readonly List<MonitorMessage> messages = new();
        private readonly Dictionary<string, (MonitorMessage Message, DateTime LastSeen)> incidentDiagnostics = new(StringComparer.Ordinal);

        public event NewLogMessage newMessage = delegate { };

        public MessageLog(DiagnosticsService? diagnostics = null)
        {
            Diagnostics = diagnostics ?? new DiagnosticsService();
        }

        public DiagnosticsService Diagnostics { get; }

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

        public IReadOnlyList<MonitorMessage> GetSnapshot() => Messages;

        public void AddMessage(MonitorMessage message)
        {
            message.Message = LimitMessageLength(message.Message);
            AddMessageCore(message);
        }

        public void AddMessage(string message, string? type = "", string? url = null, string? linkText = null)
        {
            AddMessage(new MonitorMessage(LimitMessageLength(message), type, url, linkText));
        }

        public void AddMessages(IEnumerable<MonitorMessage> messageBatch, bool preserveDisplayOrder = false)
        {
            var batch = messageBatch.ToList();
            if (batch.Count == 0)
            {
                return;
            }

            var batchId = preserveDisplayOrder ? Guid.NewGuid() : (Guid?)null;
            foreach (var message in batch)
            {
                message.Message = LimitMessageLength(message.Message);
                message.DisplayBatchId = batchId;
                message.PreserveDisplayBatchOrder = preserveDisplayOrder;
            }

            lock (gate)
            {
                messages.AddRange(batch);
                while (messages.Count > MaxMessages)
                {
                    messages.RemoveAt(0);
                }
            }

            RaiseMessageAdded(batch[^1]);
        }

        public void AddProtectedMessage(
            string message,
            string? type,
            IEnumerable<MonitorMessageProtectedValue> protectedValues,
            string? url = null,
            string? linkText = null)
        {
            var monMessage = new MonitorMessage(LimitMessageLength(message), type, url, linkText);
            foreach (var protectedValue in protectedValues
                .Where(value => !string.IsNullOrWhiteSpace(value.Label)
                    && !string.IsNullOrWhiteSpace(value.Value)))
            {
                monMessage.ProtectedValues.Add(protectedValue);
            }
            AddMessageCore(monMessage);
        }

        public DiagnosticSnapshot AddException(
            string displayMessage,
            string code,
            string operation,
            Exception exception,
            string service,
            string stage,
            string? endpoint = null,
            long? durationMilliseconds = null,
            string? incidentId = null)
        {
            var snapshot = Diagnostics.Capture(
                new DiagnosticContext(code, operation, service, stage, displayMessage, endpoint, IncidentId: incidentId),
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
                var isIncidentDiagnostic = !string.IsNullOrWhiteSpace(snapshot.IncidentId);
                var existing = isIncidentDiagnostic
                    ? incidentDiagnostics.TryGetValue(snapshot.IncidentId!, out var incidentPrevious)
                        ? incidentPrevious
                        : ((MonitorMessage Message, DateTime LastSeen)?)null
                    : recentDiagnostics.TryGetValue(snapshot.DiagnosticKey, out var recentPrevious)
                        ? recentPrevious
                        : null;

                if (existing is { } previous
                    && (isIncidentDiagnostic || now - previous.LastSeen <= DeduplicationWindow))
                {
                    var occurrenceCount = Math.Max(previous.Message.DiagnosticOccurrenceCount, snapshot.OccurrenceCount);
                    if (snapshot.OccurrenceCount >= previous.Message.DiagnosticOccurrenceCount)
                    {
                        previous.Message.DiagnosticText = snapshot.ClipboardText;
                    }
                    previous.Message.DiagnosticKey = snapshot.DiagnosticKey;
                    previous.Message.DiagnosticOccurrenceCount = occurrenceCount;
                    previous.Message.Message = $"{snapshot.DisplayMessage} (repeated {occurrenceCount} times)";
                    if (isIncidentDiagnostic)
                    {
                        incidentDiagnostics[snapshot.IncidentId!] = (previous.Message, now);
                    }
                    else
                    {
                        recentDiagnostics[snapshot.DiagnosticKey] = (previous.Message, now);
                    }
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
                    if (isIncidentDiagnostic)
                    {
                        incidentDiagnostics[snapshot.IncidentId!] = (message, now);
                    }
                    TrimRecentDiagnostics();
                    messageToRaise = message;
                }
            }

            RaiseMessageAdded(messageToRaise, isRepeat);
        }

        private void AddMessageCore(MonitorMessage message)
        {
            lock (gate)
            {
                AddToBoundedList(message);
            }

            RaiseMessageAdded(message);
        }

        private static string LimitMessageLength(string message)
        {
            if (message.Length <= MaxMessageLength)
            {
                return message;
            }

            return message[..(MaxMessageLength - ShortenedMessageSuffix.Length)] + ShortenedMessageSuffix;
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

            while (incidentDiagnostics.Count > MaxRecentDiagnostics)
            {
                var oldest = incidentDiagnostics.MinBy(entry => entry.Value.LastSeen);
                incidentDiagnostics.Remove(oldest.Key);
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
