namespace TarkovMonitor
{
    public class MonitorMessage
    {
        public string Message { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public Action? OnClick { get; set; } = null;
        public MonitorMessage(string message)
        {
            Message = message;
        }
        public MonitorMessage(string message, string? type = "", string? url = "") : this(message)
        {
            Type = type ?? "";
            Url = url ?? "";
        }

        public string RenderMessage()
        {
            if (Url.Length > 0)
            {
                return @$"<span @onclick=""openUrl"" data-url=""{Url}"">{Message}</span>";
            }
            return Message;
        }
    }
}