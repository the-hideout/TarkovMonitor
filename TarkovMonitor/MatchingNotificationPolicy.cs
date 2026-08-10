namespace TarkovMonitor;

internal static class MatchingNotificationPolicy
{
    public static bool ShouldPublish(
        bool initialRead,
        bool readingPastLogs,
        bool alreadyPublished,
        float mapLoadTime,
        float queueTime,
        DateTime? startingTime,
        bool allowCompletedFallback = false)
    {
        return !initialRead
            && !readingPastLogs
            && !alreadyPublished
            && mapLoadTime > 0
            && (allowCompletedFallback || queueTime <= 0)
            && startingTime == null;
    }
}
