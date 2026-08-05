using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovMonitor;

internal readonly record struct ParsedScreenshotPosition(
    float X,
    float Y,
    float Z,
    float Rotation);

internal static partial class WatcherFileUtilities
{
    private static readonly string[] LogFolderTimestampFormats =
    [
        "yyyy.MM.dd_H-m-s",
        "yyyy.MM.dd_HH-mm-ss",
    ];

    private const string NumberPattern = @"[+-]?(?:\d+(?:\.\d+)?|\.\d+)";

    [GeneratedRegex(
        @"^log_(?<timestamp>\d{4}\.\d{2}\.\d{2}_\d{1,2}-\d{1,2}-\d{1,2})(?:_|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LogFolderPattern();

    [GeneratedRegex(
        @"(?:^|\s)(?<type>application|push-notifications|notifications|output|traces)(?:_\d+)?\.log$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LogFilePattern();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}\]_?(?<x>" + NumberPattern + @"),\s*(?<y>" + NumberPattern + @"),\s*(?<z>" + NumberPattern + @")_?(?<rx>" + NumberPattern + @"),\s*(?<ry>" + NumberPattern + @"),\s*(?<rz>" + NumberPattern + @"),\s*(?<rw>" + NumberPattern + @")(?:_[^()]*)?\s+\(\d+\)\.png$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotPositionPattern();

    internal static bool TryResolveLogsFolder(
        string? selectedPath,
        out string logsPath,
        out string failureReason)
    {
        logsPath = "";
        failureReason = "No folder was selected.";
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return false;
        }

        try
        {
            var candidate = NormalizeDirectoryPath(selectedPath.Trim().Trim('"'));
            if (!Directory.Exists(candidate))
            {
                failureReason = "The selected folder no longer exists.";
                return false;
            }

            var directoryName = Path.GetFileName(candidate);
            if (TryGetLogFolderTimestamp(candidate, out _))
            {
                var parent = Directory.GetParent(candidate)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    logsPath = NormalizeDirectoryPath(parent);
                    failureReason = "";
                    return true;
                }
            }

            if (string.Equals(directoryName, "Logs", StringComparison.OrdinalIgnoreCase)
                || ContainsLogSessionFolder(candidate))
            {
                logsPath = NormalizeDirectoryPath(candidate);
                failureReason = "";
                return true;
            }

            foreach (var relativePath in new[] { "Logs", Path.Combine("build", "Logs") })
            {
                var nestedLogsPath = Path.Combine(candidate, relativePath);
                if (!Directory.Exists(nestedLogsPath))
                {
                    continue;
                }

                logsPath = NormalizeDirectoryPath(nestedLogsPath);
                failureReason = "";
                return true;
            }

            failureReason = "Select the Escape from Tarkov Logs folder or the EFT install folder that contains it.";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            failureReason = $"The selected folder cannot be used: {ex.Message}";
            return false;
        }
    }

    internal static bool TryGetLogFolderTimestamp(string folderPath, out DateTime timestamp)
    {
        timestamp = default;
        var match = LogFolderPattern().Match(Path.GetFileName(folderPath));
        return match.Success
            && DateTime.TryParseExact(
                match.Groups["timestamp"].Value,
                LogFolderTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp);
    }

    internal static bool TryGetLogType(string? path, out GameLogType logType)
    {
        logType = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var filename = Path.GetFileName(path);
        var match = LogFilePattern().Match(filename);
        if (!match.Success)
        {
            return false;
        }

        logType = match.Groups["type"].Value.ToLowerInvariant() switch
        {
            "application" => GameLogType.Application,
            "push-notifications" or "notifications" => GameLogType.Notifications,
            "output" => GameLogType.Output,
            "traces" => GameLogType.Traces,
            _ => default,
        };
        return true;
    }

    internal static bool TryParseScreenshotPosition(
        string? filename,
        out ParsedScreenshotPosition position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        var match = ScreenshotPositionPattern().Match(filename);
        if (!match.Success
            || !TryParseFinite(match.Groups["x"].Value, out var x)
            || !TryParseFinite(match.Groups["y"].Value, out var y)
            || !TryParseFinite(match.Groups["z"].Value, out var z)
            || !TryParseFinite(match.Groups["rx"].Value, out var rotationX)
            || !TryParseFinite(match.Groups["ry"].Value, out var rotationY)
            || !TryParseFinite(match.Groups["rz"].Value, out var rotationZ)
            || !TryParseFinite(match.Groups["rw"].Value, out var rotationW))
        {
            return false;
        }

        position = new(
            x,
            y,
            z,
            QuaternionToYaw(rotationX, rotationY, rotationZ, rotationW));
        return true;
    }

    internal static bool TryGetContainedScreenshotPath(
        string screenshotsFolder,
        string? filename,
        out string screenshotPath)
    {
        screenshotPath = "";
        if (string.IsNullOrWhiteSpace(screenshotsFolder)
            || string.IsNullOrWhiteSpace(filename)
            || !string.Equals(filename, Path.GetFileName(filename), StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(filename), ".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = NormalizeDirectoryPath(screenshotsFolder);
            var candidate = Path.GetFullPath(Path.Combine(root, filename));
            if (!string.Equals(
                Path.GetDirectoryName(candidate)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            screenshotPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static float QuaternionToYaw(float x, float y, float z, float w)
    {
        var sinYaw = 2.0f * ((w * y) + (x * z));
        var cosYaw = 1.0f - (2.0f * ((y * y) + (z * z)));
        return (float)(Math.Atan2(sinYaw, cosYaw) * (180.0 / Math.PI));
    }

    private static bool ContainsLogSessionFolder(string candidate)
    {
        try
        {
            return Directory.EnumerateDirectories(candidate)
                .Any(folder => TryGetLogFolderTimestamp(folder, out _));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryParseFinite(string value, out float number)
    {
        return float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number)
            && float.IsFinite(number);
    }
}
