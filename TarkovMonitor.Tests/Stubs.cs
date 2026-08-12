namespace TarkovMonitor
{
    internal enum TaskStatus
    {
        None = 0,
        Started = 10,
        Failed = 11,
        Finished = 12,
    }

    internal enum MessageType
    {
        TaskStarted = 10,
        TaskFailed = 11,
        TaskFinished = 12,
    }

    internal enum ProfileType
    {
        PVE,
        Regular,
        PvpSeason,
        Unknown,
    }

    internal sealed class Profile
    {
        public string Id { get; set; } = "";
        public ProfileType Type { get; set; }
        public string AccountId { get; set; } = "";
    }

    internal sealed class LogDetails
    {
        public Profile Profile { get; set; } = new();
        public int AccountId { get; set; }
        public DateTime Date { get; set; }
        public Version Version { get; set; } = new(0, 0);
        public string Folder { get; set; } = "";
    }

    internal static class TarkovTracker
    {
        internal sealed class ProgressResponseTask
        {
            public string id { get; set; } = "";
            public bool complete { get; set; }
            public bool invalid { get; set; }
            public bool failed { get; set; }
            public bool? active { get; set; }
        }

        internal static bool IsSupportedOrgToken(string? token)
        {
            var value = token?.Trim() ?? "";
            return value.Length == 22
                && value[3] == '_'
                && value[..3].ToUpperInvariant() is "PVE" or "PVP" or "SZN"
                && value[4..].All(character => Uri.IsHexDigit(character));
        }
    }
}
