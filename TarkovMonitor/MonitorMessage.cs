using MudBlazor;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Timers;

namespace TarkovMonitor
{
    public class MonitorMessage
    {
        public string Message { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public Action? OnClick { get; set; } = null;
        public ObservableCollection<MonitorMessageButton> Buttons { get; set; } = new();
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
        public MonitorMessage(string message, string? type = "", string? url = "") : this(message)
        {
            Type = type ?? "";
            Url = url ?? "";
            if (Type == "exception")
            {
                Buttons.Add(new("Copy", () => {
                    Clipboard.SetText(Message);
                }, Icons.Material.Filled.CopyAll));
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

        private void ButtonExpired(object? sender, EventArgs e)
        {
            if (sender == null)
            {
                return;
            }
            Buttons.Remove((MonitorMessageButton)sender);
        }
    }

    public class MonitorMessageButton
    {
        public string Text { get; set; }
        public string Icon { get; set; } = "";
        public MudBlazor.Color Color { get; set; } = MudBlazor.Color.Default;
        public Action? OnClick { get; set; }
        public bool Disabled { get; set; } = false;
        public MonitorMessageButtonConfirm? Confirm { get; set; }
        private System.Timers.Timer? buttonTimer;
        private double? timeout = null;
        public double? Timeout {
            get
            {
                return timeout;
            }
            set
            {
                timeout = value;
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
                        AutoReset = true,
                        Enabled = true,
                    };
                    buttonTimer.Elapsed += (object? sender, ElapsedEventArgs e) =>
                    {
                        Expired?.Invoke(this, e);
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
        public MonitorMessageButton(string text, string icon = "") : this(text, null, icon) { }
        public void Expire()
        {
            buttonTimer?.Stop();
            Expired?.Invoke(this, new());
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
}