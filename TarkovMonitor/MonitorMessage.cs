namespace TarkovMonitor
{
    public class MonitorMessage
    {
        public string Message { get; set; }
        public DateTime Time { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
        public MonitorMessage(string message, string? type = null, string? url = null)
        {
            Message = message;
            Time = DateTime.Now;
            if (type == null)
            {
                Type = "";
            }
            else
            {
                Type = type;
            }
            if (url == null)
            {
                Url = "";
            }
            else
            {
                Url = url;
            }
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