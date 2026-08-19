using MudBlazor;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Timers;

namespace TarkovMonitor
{
    public sealed class MonitorMessageCollection<T>
    {
        private readonly object syncRoot = new();
        private readonly List<T> items = new();
        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return items.Count;
                }
            }
        }

        public IReadOnlyList<T> GetSnapshot()
        {
            lock (syncRoot)
            {
                return items.ToList();
            }
        }

        public void Add(T item)
        {
            lock (syncRoot)
            {
                var index = items.Count;
                items.Add(item);
                CollectionChanged?.Invoke(
                    this,
                    new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
            }
        }

        public bool Remove(T item)
        {
            lock (syncRoot)
            {
                var index = items.IndexOf(item);
                if (index < 0)
                {
                    return false;
                }

                var removedItem = items[index];
                items.RemoveAt(index);
                CollectionChanged?.Invoke(
                    this,
                    new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removedItem, index));
                return true;
            }
        }

        public void Clear()
        {
            lock (syncRoot)
            {
                if (items.Count == 0)
                {
                    return;
                }

                var removedItems = items.ToList();
                items.Clear();
                CollectionChanged?.Invoke(
                    this,
                    new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removedItems, 0));
            }
        }
    }

    public readonly record struct MonitorMessageActionResult(bool Succeeded, string Message);

    public class MonitorMessage
    {
        internal Guid? DisplayBatchId { get; set; }
        internal bool PreserveDisplayBatchOrder { get; set; }
        public string Message { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public string? DiagnosticText { get; set; }
        public string? DiagnosticKey { get; set; }
        public int DiagnosticOccurrenceCount { get; set; } = 1;
        public string LinkText { get; set; } = "";
        public Action? OnClick { get; set; } = null;
        public MonitorMessageCollection<MonitorMessageButton> Buttons { get; } = new();
        public MonitorMessageCollection<MonitorMessageSelect> Selects { get; } = new();
        public List<MonitorMessageProtectedValue> ProtectedValues { get; } = new();

        /// <summary>
        /// Raised when the text of an already displayed message changes, so a
        /// long-running action can report progress in place instead of adding a
        /// new message for every step.
        /// </summary>
        public event EventHandler? Changed;

        public void NotifyChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public MonitorMessage(string message)
        {
            Message = message;
            Buttons.CollectionChanged += (object? sender, NotifyCollectionChangedEventArgs e) => {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    if (e.NewItems == null)
                    {
                        return;
                    }
                    foreach (MonitorMessageButton button in e.NewItems.Cast<MonitorMessageButton>().ToList())
                    {
                        button.Expired += ButtonExpired;
                    }
                }
                if (e.Action == NotifyCollectionChangedAction.Remove)
                {
                    if (e.OldItems == null)
                    {
                        return;
                    }
                    foreach (MonitorMessageButton button in e.OldItems.Cast<MonitorMessageButton>().ToList())
                    {
                        button.Expired -= ButtonExpired;
                    }
                }
            };
        }
        public MonitorMessage(string message, string? type = "", string? url = "", string? linkText = "", string? diagnosticText = null) : this(message)
        {
            Type = type ?? "";
            Url = url ?? "";
            DiagnosticText = diagnosticText;
            LinkText = linkText ?? "";
            if (Type == "exception")
            {
                Buttons.Add(new MonitorMessageButton("Copy diagnostics", CopyDiagnostics, Icons.Material.Filled.CopyAll)
                {
                    Color = MudBlazor.Color.Info,
                });
                Buttons.Add(new("Report", () => {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "https://github.com/the-hideout/TarkovMonitor/issues",
                        UseShellExecute = true,
                    };
                    Process.Start(psi);
                }, Icons.Material.Filled.BugReport));
            }
        }

        private MonitorMessageActionResult CopyDiagnostics()
        {
            try
            {
                Clipboard.SetText(DiagnosticText ?? Message);
                return new(true, "Sanitized diagnostics copied to the clipboard.");
            }
            catch
            {
                return new(false, "Diagnostics could not be copied to the clipboard. Try again.");
            }
        }

        private void ButtonExpired(object? sender, EventArgs e)
        {
            if (sender == null)
            {
                return;
            }
            Buttons.Remove((MonitorMessageButton)sender);
        }
    }

    public sealed class MonitorMessageProtectedValue
    {
        public string Label { get; }
        public string Value { get; }

        public MonitorMessageProtectedValue(string label, string value)
        {
            Label = label;
            Value = value;
        }
    }

    public class MonitorMessageButton
    {
        public string Text { get; set; }
        public string Icon { get; set; } = "";
        public MudBlazor.Color Color { get; set; } = MudBlazor.Color.Default;
        public Action? OnClick { get; set; }
        public Func<MonitorMessageActionResult>? ResultAction { get; set; }
        public bool Disabled { get; set; } = false;
        public MonitorMessageButtonConfirm? Confirm { get; set; }
        private System.Timers.Timer? buttonTimer;
        private double? timeout = null;
        private DateTimeOffset? expiresAtUtc;
        private int expirationRaised;
        public bool IsExpired => expiresAtUtc.HasValue && DateTimeOffset.UtcNow >= expiresAtUtc.Value;
        public double? Timeout {
            get
            {
                return timeout;
            }
            set
            {
                timeout = value;
                expiresAtUtc = value is > 0
                    ? DateTimeOffset.UtcNow.AddMilliseconds(value.Value)
                    : null;
                Interlocked.Exchange(ref expirationRaised, 0);
                if (buttonTimer != null)
                {
                    buttonTimer.Stop();
                    buttonTimer.Dispose();
                }
                if (value == null || value == 0)
                {
                    buttonTimer = null;
                }
                else
                {
                    buttonTimer = new(timeout ?? 0) {
                        AutoReset = false,
                        Enabled = true,
                    };
                    buttonTimer.Elapsed += (object? sender, ElapsedEventArgs e) =>
                    {
                        RaiseExpired(e);
                    };

                }
            }
        }
        public event EventHandler? Expired;
        public MonitorMessageButton(string text, Action? onClick = null, string icon = "")
        {
            Text = text;
            Icon = icon;
            OnClick = onClick;
        }

        public MonitorMessageButton(string text, Func<MonitorMessageActionResult> resultAction, string icon = "")
        {
            Text = text;
            Icon = icon;
            ResultAction = resultAction;
        }
        public MonitorMessageButton(string text, string icon = "") : this(text, null, icon) { }
        public void Expire()
        {
            buttonTimer?.Stop();
            expiresAtUtc = DateTimeOffset.UtcNow;
            RaiseExpired(EventArgs.Empty);
        }

        private void RaiseExpired(EventArgs args)
        {
            if (Interlocked.Exchange(ref expirationRaised, 1) == 0)
            {
                Expired?.Invoke(this, args);
            }
        }
    }

    public class MonitorMessageButtonConfirm
    {
        public string Title { get; set; } = "Confirm";
        public string Message { get; set; }
        public string YesText { get; set; }
        public string CancelText { get; set; } = "Cancel";
        public MonitorMessageButtonConfirm(string title, string message, string yesText, string cancelText)
        {
            Title = title;
            Message = message;
            YesText = yesText;
            CancelText = cancelText;
        }
    }

    public class MonitorMessageSelect
    {
        public List<MonitorMessageSelectOption> Options { get; set; } = new();
        public event EventHandler<MonitorMessageSelectChangedEventArgs>? SelectionChanged;
        public MonitorMessageSelectOption? Selected { get; private set; }
        public string Placeholder { get; set; } = "";
        public void ChangeSelection(MonitorMessageSelectOption selected)
        {
            Selected = selected;
            SelectionChanged?.Invoke(this, new MonitorMessageSelectChangedEventArgs() { Selected = selected });
        }
    }
    
    public class MonitorMessageSelectOption
    {
        public string Text { get; set; }
        public string Value { get; set; }
        override public string ToString()
        {
            return Text;
        }
        public MonitorMessageSelectOption(string text, string value)
        {
            Text = text;
            Value = value;
        }
    }

    public class MonitorMessageSelectChangedEventArgs : EventArgs
    {
        public MonitorMessageSelectOption Selected { get; set; }
    }
}
