using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovMonitor;

public sealed record DiagnosticContext(
    string Code,
    string Operation,
    string Service,
    string Stage,
    string DisplayMessage,
    string? Endpoint = null,
    string Outcome = "failure",
    string? IncidentId = null);

public sealed class DiagnosticSnapshot
{
    public string EventId { get; init; } = "";
    public string Code { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Service { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Outcome { get; init; } = "failure";
    public string DisplayMessage { get; init; } = "";
    public string? Endpoint { get; init; }
    public string? IncidentId { get; init; }
    public DateTime TimestampUtc { get; init; }
    public long? DurationMilliseconds { get; init; }
    public string ApplicationVersion { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string ExceptionType { get; init; } = "";
    public string HResult { get; init; } = "";
    public string DiagnosticKey { get; init; } = "";
    public string ClipboardText { get; init; } = "";
    public int OccurrenceCount { get; internal set; } = 1;
}

public static class DiagnosticRedactor
{
    private static readonly Regex ApiKey = new(
        @"\b(?:PVE|PVP|SZN)_[0-9A-Fa-f]{18}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveKeyValue = new(
        @"(?ix)(?<key>\b(?:authorization|bearer|token|api[_-]?key|sessionid|accountid|profileid|remoteid|cookie)\b\s*(?:[:=]|=>)\s*)(?<value>Bearer\s+[^\s,;}&]+|[^\s,;}&]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DictionaryKey = new(
        @"(?ix)(?<key>\bkey\b\s*:\s*)(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonSensitiveKeyValue = new(
        @"(?ix)(?<key>(?<![a-z0-9_])""?(?:authorization|bearer|token|api[_-]?key|sessionid|accountid|profileid|remoteid|cookie)""?\s*:\s*)""?(?<value>[^""\s,}]+)""?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerToken = new(
        @"(?i)\bBearer\s+[^\s,;}""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LocalPath = new(
        @"(?ix)(?<![a-z0-9])(?:(?:file:///+)?[a-z]:[\\/]|\\\\[^\\/\s]+[\\/])[^<>\r\n""'`|]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QueryString = new(
        @"(?i)(?<url>https?://[^\s?]+)\?[^\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SensitivePathSegment = new(
        @"(?i)(?<prefix>/(?:account|accountid|profile|profileid|player|user)(?:/|=))(?<value>[^/\s?#]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IPv4 = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IPv6Candidate = new(
        @"(?i)(?<![0-9a-f])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:]{0,32}(?![0-9a-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string? value, int maxLength = 16_384)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sanitized = value.Replace("%USERPROFILE%", "<user-profile>", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.Replace("%LOCALAPPDATA%", "<local-app-data>", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.Replace("%APPDATA%", "<app-data>", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.Replace("%TEMP%", "<temp>", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.Replace("%TMP%", "<temp>", StringComparison.OrdinalIgnoreCase);
        sanitized = ApiKey.Replace(sanitized, "[REDACTED_API_KEY]");
        sanitized = SensitiveKeyValue.Replace(sanitized, match => $"{match.Groups["key"].Value}[REDACTED]");
        sanitized = DictionaryKey.Replace(sanitized, match => $"{match.Groups["key"].Value}[REDACTED_ID]");
        sanitized = JsonSensitiveKeyValue.Replace(sanitized, match => $"{match.Groups["key"].Value}\"[REDACTED]\"");
        sanitized = BearerToken.Replace(sanitized, "Bearer [REDACTED]");
        sanitized = QueryString.Replace(sanitized, "$url?[REDACTED_QUERY]");
        sanitized = SensitivePathSegment.Replace(sanitized, "${prefix}[REDACTED_ID]");
        sanitized = LocalPath.Replace(sanitized, "[REDACTED_LOCAL_PATH]");
        sanitized = IPv4.Replace(sanitized, match =>
        {
            var precedingText = sanitized[..match.Index];
            return Regex.IsMatch(precedingText, @"(?i)\bversion\s*:\s*$")
                ? match.Value
                : "[REDACTED_IP]";
        });
        sanitized = IPv6Candidate.Replace(sanitized, match =>
            IPAddress.TryParse(match.Value, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? "[REDACTED_IP]"
                    : match.Value);

        if (sanitized.Length <= maxLength)
        {
            return sanitized;
        }

        return sanitized[..maxLength] + "\n[TRUNCATED]";
    }

    public static string SanitizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "";
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "[UNPARSED_ENDPOINT]";
        }

        var host = uri.Host.Contains(':', StringComparison.Ordinal)
            ? $"[{uri.Host}]"
            : uri.Host;
        return Sanitize($"{uri.Scheme}://{host}:{uri.Port}", 512);
    }
}

public sealed class DiagnosticsService
{
    private const long MaxDiagnosticFileBytes = 1_000_000;
    private const int MaxDiagnosticFiles = 5;
    private readonly object gate = new();
    private readonly Dictionary<string, int> occurrenceCounts = new(StringComparer.Ordinal);

    public DiagnosticsService(string? diagnosticsDirectory = null)
    {
        DiagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TarkovMonitor",
                "Diagnostics");
    }

    public string DiagnosticsDirectory { get; }

    public static long ElapsedMilliseconds(DateTime startedUtc)
    {
        var elapsed = (DateTime.UtcNow - startedUtc).TotalMilliseconds;
        return elapsed <= 0 ? 0 : (long)elapsed;
    }

    public DiagnosticSnapshot Capture(DiagnosticContext context, Exception? exception = null, long? durationMilliseconds = null)
    {
        var timestamp = DateTime.UtcNow;
        var eventId = Guid.NewGuid().ToString("N")[..12];
        var exceptionDetails = GetExceptionDetails(exception);
        var exceptionType = exceptionDetails.Count == 0 ? "" : exceptionDetails[0].Type;
        var hResult = exceptionDetails.Count == 0 ? "" : exceptionDetails[0].HResult;
        var sanitizedCode = DiagnosticRedactor.Sanitize(context.Code, 128);
        var sanitizedOperation = DiagnosticRedactor.Sanitize(context.Operation, 256);
        var sanitizedIncidentId = DiagnosticRedactor.Sanitize(context.IncidentId, 128);
        var diagnosticKey = string.IsNullOrWhiteSpace(sanitizedIncidentId)
            ? string.Join("|", sanitizedCode, sanitizedOperation, exceptionType, hResult)
            : $"incident:{sanitizedIncidentId}";

        int occurrenceCount;
        lock (gate)
        {
            occurrenceCounts.TryGetValue(diagnosticKey, out occurrenceCount);
            occurrenceCount++;
            occurrenceCounts[diagnosticKey] = occurrenceCount;
        }

        var snapshot = new DiagnosticSnapshot
        {
            EventId = eventId,
            Code = sanitizedCode,
            Operation = sanitizedOperation,
            Service = DiagnosticRedactor.Sanitize(context.Service, 128),
            Stage = DiagnosticRedactor.Sanitize(context.Stage, 128),
            Outcome = DiagnosticRedactor.Sanitize(context.Outcome, 64),
            DisplayMessage = DiagnosticRedactor.Sanitize(context.DisplayMessage, 512),
            Endpoint = DiagnosticRedactor.SanitizeEndpoint(context.Endpoint),
            IncidentId = sanitizedIncidentId,
            TimestampUtc = timestamp,
            DurationMilliseconds = durationMilliseconds is long measuredDuration
                ? Math.Max(0, measuredDuration)
                : null,
            ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown",
            Runtime = DiagnosticRedactor.Sanitize(RuntimeInformation.FrameworkDescription, 128),
            OperatingSystem = DiagnosticRedactor.Sanitize(RuntimeInformation.OSDescription, 256),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ExceptionType = exceptionType,
            HResult = hResult,
            DiagnosticKey = diagnosticKey,
            OccurrenceCount = occurrenceCount,
            ClipboardText = BuildClipboardText(
                eventId,
                context,
                timestamp,
                durationMilliseconds,
                exceptionDetails,
                occurrenceCount),
        };

        Persist(snapshot, exceptionDetails);
        return snapshot;
    }

    private static List<ExceptionDetail> GetExceptionDetails(Exception? exception)
    {
        var details = new List<ExceptionDetail>();
        var current = exception;
        var depth = 0;
        while (current != null && depth++ < 12)
        {
            var socketError = current is SocketException socket
                ? $"{socket.SocketErrorCode} ({socket.ErrorCode})"
                : "";
            details.Add(new ExceptionDetail(
                current.GetType().FullName ?? current.GetType().Name,
                DiagnosticRedactor.Sanitize(current.Message),
                $"0x{current.HResult:X8}",
                socketError,
                DiagnosticRedactor.Sanitize(current.StackTrace, 32_768)));
            current = current.InnerException;
        }

        if (current != null)
        {
            details.Add(new ExceptionDetail("<additional-inner-exceptions>", "[TRUNCATED]", "", "", "[TRUNCATED]"));
        }

        return details;
    }

    private static string BuildClipboardText(
        string eventId,
        DiagnosticContext context,
        DateTime timestamp,
        long? durationMilliseconds,
        IReadOnlyList<ExceptionDetail> details,
        int occurrenceCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("TarkovMonitor diagnostics");
        builder.AppendLine($"Diagnostic ID: {eventId}");
        builder.AppendLine($"Code: {DiagnosticRedactor.Sanitize(context.Code, 128)}");
        builder.AppendLine($"Operation: {DiagnosticRedactor.Sanitize(context.Operation, 256)}");
        builder.AppendLine($"Service: {DiagnosticRedactor.Sanitize(context.Service, 128)}");
        builder.AppendLine($"Stage: {DiagnosticRedactor.Sanitize(context.Stage, 128)}");
        builder.AppendLine($"Outcome: {DiagnosticRedactor.Sanitize(context.Outcome, 64)}");
        builder.AppendLine($"Endpoint: {DiagnosticRedactor.SanitizeEndpoint(context.Endpoint)}");
        builder.AppendLine($"Occurred (UTC): {timestamp:O}");
        var durationText = durationMilliseconds is long measuredDuration
            ? Math.Max(0, measuredDuration).ToString()
            : "[NOT_MEASURED]";
        builder.AppendLine($"Duration (ms): {durationText}");
        builder.AppendLine($"Occurrences: {occurrenceCount}");
        builder.AppendLine($"Application version: {Assembly.GetEntryAssembly()?.GetName().Version ?? Assembly.GetExecutingAssembly().GetName().Version}");
        builder.AppendLine($"Runtime: {DiagnosticRedactor.Sanitize(RuntimeInformation.FrameworkDescription, 128)}");
        builder.AppendLine($"Operating system: {DiagnosticRedactor.Sanitize(RuntimeInformation.OSDescription, 256)}");
        builder.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine();
        builder.AppendLine("Exception chain:");

        if (details.Count == 0)
        {
            builder.AppendLine("  None");
        }
        else
        {
            for (var index = 0; index < details.Count; index++)
            {
                var detail = details[index];
                builder.AppendLine($"  [{index}] Type: {detail.Type}");
                builder.AppendLine($"      Message: {detail.Message}");
                builder.AppendLine($"      HResult: {detail.HResult}");
                if (!string.IsNullOrWhiteSpace(detail.SocketError))
                {
                    builder.AppendLine($"      Socket error: {detail.SocketError}");
                }
                builder.AppendLine("      Details:");
                builder.AppendLine(detail.ToStringValue);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Privacy: API tokens, authorization headers, request bodies, IP addresses, account/profile identifiers, and full local paths are excluded or redacted. Review before sharing.");
        return DiagnosticRedactor.Sanitize(builder.ToString(), 64_000);
    }

    private void Persist(DiagnosticSnapshot snapshot, IReadOnlyList<ExceptionDetail> details)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(DiagnosticsDirectory);
                RotateIfNeeded("diagnostics.jsonl");
                RotateIfNeeded("analytics.jsonl");

                var record = new
                {
                    snapshot.EventId,
                    snapshot.Code,
                    snapshot.Operation,
                    snapshot.Service,
                    snapshot.Stage,
                    snapshot.Outcome,
                    snapshot.DisplayMessage,
                    snapshot.Endpoint,
                    snapshot.IncidentId,
                    snapshot.TimestampUtc,
                    snapshot.DurationMilliseconds,
                    snapshot.ApplicationVersion,
                    snapshot.Runtime,
                    snapshot.OperatingSystem,
                    snapshot.Architecture,
                    snapshot.ExceptionType,
                    snapshot.HResult,
                    snapshot.DiagnosticKey,
                    snapshot.OccurrenceCount,
                    ExceptionChain = details.Select(detail => new
                    {
                        detail.Type,
                        detail.Message,
                        detail.HResult,
                        detail.SocketError,
                    }),
                };
                File.AppendAllText(
                    Path.Combine(DiagnosticsDirectory, "diagnostics.jsonl"),
                    JsonSerializer.Serialize(record) + Environment.NewLine,
                    Encoding.UTF8);

                var analytics = new
                {
                    snapshot.Code,
                    snapshot.Operation,
                    snapshot.Service,
                    snapshot.Stage,
                    snapshot.Outcome,
                    snapshot.TimestampUtc,
                    snapshot.DurationMilliseconds,
                    snapshot.ApplicationVersion,
                };
                File.AppendAllText(
                    Path.Combine(DiagnosticsDirectory, "analytics.jsonl"),
                    JsonSerializer.Serialize(analytics) + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become a new application failure.
        }
    }

    private void RotateIfNeeded(string fileName)
    {
        var path = Path.Combine(DiagnosticsDirectory, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length < MaxDiagnosticFileBytes)
        {
            return;
        }

        for (var index = MaxDiagnosticFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            var target = $"{path}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, target, true);
            }
        }

        File.Move(path, $"{path}.1", true);
    }

    private sealed record ExceptionDetail(
        string Type,
        string Message,
        string HResult,
        string SocketError,
        string ToStringValue);
}
