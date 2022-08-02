namespace TarkovMonitor
{
    public class LogLine
    {
        public string Message { get; set; }
        public DateTime Time { get; set; }
        public string Type { get; set; }
        public LogLine(string message, string? type = null)
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