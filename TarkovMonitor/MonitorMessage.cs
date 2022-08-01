namespace TarkovMonitor
{
    public class MonitorMessage
    {
        public string Message { get; set; }
        public DateTime Time { get; set; }
        public string Type { get; set; }
        public MonitorMessage(string message, string? type = null)
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
        }
    }
}