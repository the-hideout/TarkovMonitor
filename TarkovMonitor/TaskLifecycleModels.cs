namespace TarkovMonitor
{
    public enum MessageType
    {
        PlayerMessage = 1,
        Insurance = 2,
        FleaMarket = 4,
        InsuranceReturn = 8,
        TaskStarted = 10,
        TaskFailed = 11,
        TaskFinished = 12,
        TwitchDrop = 13,
    }

    public enum TaskStatus
    {
        None = 0,
        Started = 10,
        Failed = 11,
        Finished = 12,
    }

    public enum ProfileType
    {
        PVE,
        Regular,
        PvpSeason,
        Unknown,
    }

    public static class ProfileTypeExtensions
    {
        public static string ToApiString(this ProfileType profileType) => profileType switch
        {
            ProfileType.PvpSeason => "pvp-season",
            ProfileType.Unknown => "unknown",
            _ => profileType.ToString().ToLower(),
        };

        public static string ToPlayersApiString(this ProfileType profileType) => profileType switch
        {
            ProfileType.PvpSeason => "pvp-season",
            ProfileType.Regular => "profile",
            ProfileType.Unknown => "unknown",
            _ => profileType.ToString().ToLower(),
        };
    }

    public class Profile
    {
        public string Id { get; set; } = "";
        public ProfileType Type { get; set; } = ProfileType.Unknown;
        public string AccountId { get; set; } = "";

        public Profile Snapshot() => new()
        {
            Id = Id,
            Type = Type,
            AccountId = AccountId,
        };
    }

    public class LogDetails
    {
        public Profile Profile { get; set; } = new();
        public int AccountId { get; set; }
        public DateTime Date { get; set; }
        public Version Version { get; set; } = new(0, 0);
        public string Folder { get; set; } = "";
    }

    public class TrackerTaskProgress
    {
        public string id { get; set; } = "";
        public bool complete { get; set; }
        public bool invalid { get; set; }
        public bool failed { get; set; }
        public bool? active { get; set; }
    }

    internal readonly record struct ProfileModeTransition(
        bool Recognized,
        bool Changed,
        bool ProfileReady,
        ProfileType Mode);

    internal static class ProfileIdentity
    {
        internal static bool TryParseMode(string? rawMode, out ProfileType mode)
        {
            if (Enum.TryParse(rawMode?.Trim(), true, out mode)
                && Enum.IsDefined(mode)
                && mode != ProfileType.Unknown)
            {
                return true;
            }

            mode = ProfileType.Unknown;
            return false;
        }

        internal static ProfileModeTransition ApplyMode(Profile profile, string? rawMode)
        {
            var previousMode = profile.Type;
            if (!TryParseMode(rawMode, out var mode))
            {
                if (previousMode != ProfileType.Unknown)
                {
                    profile.Id = "";
                    profile.AccountId = "";
                }
                profile.Type = ProfileType.Unknown;
                return new(false, previousMode != ProfileType.Unknown, false, ProfileType.Unknown);
            }

            if (previousMode != ProfileType.Unknown && previousMode != mode)
            {
                profile.Id = "";
                profile.AccountId = "";
            }
            profile.Type = mode;
            return new(
                true,
                previousMode != mode,
                !string.IsNullOrWhiteSpace(profile.Id),
                mode);
        }
    }
}
