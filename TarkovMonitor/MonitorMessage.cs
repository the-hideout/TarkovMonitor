using MudBlazor;
using System.Diagnostics;

namespace TarkovMonitor
{
    public class MonitorMessage
    {
        public string Message { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public Action? OnClick { get; set; } = null;
        public List<MonitorMessageButton> Buttons { get; set; } = new();
        public MonitorMessage(string message)
        {
            Message = message;
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
    }

    public class MonitorMessageButton
    {
        public string Text { get; set; }
        public string Icon { get; set; } = "";
        public MudBlazor.Color Color { get; set; } = MudBlazor.Color.Default;
        public Action? OnClick { get; set; }
        public bool Disabled { get; set; } = false;
        public MonitorMessageButton(string text, Action? onClick = null, string icon = "")
        {
            Text = text;
            Icon = icon;
            OnClick = onClick;
        }
        public MonitorMessageButton(string text, string icon = "") : this(text, null, icon) { }
    }
}