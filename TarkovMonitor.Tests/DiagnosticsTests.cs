using System.Net;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace TarkovMonitor.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void RedactorRemovesCredentialsIdentityNetworkAndQueryData()
    {
        const string apiKey = "PVE_0123456789ABCDEF12";
        var value = $"Authorization: Bearer secret-token token={apiKey} {{\"accountId\":\"123456\",\"profileId\":\"abc\"}} https://api.example.test/token?key=secret 192.0.2.44 C:\\Users\\alice\\Documents";

        var sanitized = DiagnosticRedactor.Sanitize(value);

        Assert.DoesNotContain(apiKey, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.44", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::44", DiagnosticRedactor.Sanitize("remote endpoint 2001:db8::44"), StringComparison.Ordinal);
        Assert.DoesNotContain("alice", sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_QUERY]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("9876543210", DiagnosticRedactor.Sanitize("https://api.example.test/profile/9876543210"), StringComparison.Ordinal);
        Assert.Contains("Application version: 1.13.0.0", DiagnosticRedactor.Sanitize("Application version: 1.13.0.0"), StringComparison.Ordinal);
        Assert.Contains("Occurred: 2026-08-10T16:08:47.7398985Z", DiagnosticRedactor.Sanitize("Occurred: 2026-08-10T16:08:47.7398985Z"), StringComparison.Ordinal);
        Assert.Equal("https://api.example.test:443", DiagnosticRedactor.SanitizeEndpoint("https://api.example.test/token?key=secret"));
    }

    [Fact]
    public void CapturePreservesInnerExceptionChainWithoutPersistingRawSensitiveValues()
    {
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "TarkovMonitor-DiagnosticsTests", Guid.NewGuid().ToString("N"));
        try
        {
            const string apiKey = "PVE_0123456789ABCDEF12";
            var inner = new HttpRequestException($"TLS failed for 192.0.2.44 token={apiKey}", null, HttpStatusCode.BadGateway);
            var outer = new InvalidOperationException("Tracker request failed", inner);
            var service = new DiagnosticsService(diagnosticsDirectory);

            var snapshot = service.Capture(
                new DiagnosticContext("TM-TEST-001", "TestToken", "TarkovTracker", "TLS", "The request failed.", "https://api.example.test/token?key=secret"),
                outer,
                42);

            Assert.Contains("InvalidOperationException", snapshot.ClipboardText, StringComparison.Ordinal);
            Assert.Contains("HttpRequestException", snapshot.ClipboardText, StringComparison.Ordinal);
            Assert.Contains("[REDACTED_IP]", snapshot.ClipboardText, StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, snapshot.ClipboardText, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", snapshot.ClipboardText, StringComparison.Ordinal);
            Assert.Contains("HResult:", snapshot.ClipboardText, StringComparison.Ordinal);

            var diagnosticsFile = File.ReadAllText(Path.Combine(diagnosticsDirectory, "diagnostics.jsonl"));
            var analyticsFile = File.ReadAllText(Path.Combine(diagnosticsDirectory, "analytics.jsonl"));
            Assert.Contains("HttpRequestException", diagnosticsFile, StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, diagnosticsFile, StringComparison.Ordinal);
            Assert.DoesNotContain("ExceptionChain", analyticsFile, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", analyticsFile, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(diagnosticsDirectory))
            {
                Directory.Delete(diagnosticsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void MatchingNotificationPolicyCoversNormalLateAndFallbackPaths()
    {
        Assert.True(MatchingNotificationPolicy.ShouldPublish(false, false, false, 2.5f, 0, null));
        Assert.True(MatchingNotificationPolicy.ShouldPublish(false, false, false, 2.5f, 12, null, allowCompletedFallback: true));
        Assert.False(MatchingNotificationPolicy.ShouldPublish(false, false, true, 2.5f, 0, null));
        Assert.False(MatchingNotificationPolicy.ShouldPublish(true, false, false, 2.5f, 0, null));
        Assert.False(MatchingNotificationPolicy.ShouldPublish(false, true, false, 2.5f, 0, null));
        Assert.False(MatchingNotificationPolicy.ShouldPublish(false, false, false, 2.5f, 12, null));
        Assert.False(MatchingNotificationPolicy.ShouldPublish(false, false, false, 2.5f, 0, DateTime.UtcNow));
    }

    [Fact]
    public void TarkovDevTraderDtoMatchesCurrentSingleDataEnvelope()
    {
        const string payload = "{\"data\":{\"54cb50c76803fa8b248b4571\":{\"id\":\"54cb50c76803fa8b248b4571\",\"name\":\"Prapor\",\"normalizedName\":\"prapor\",\"reputationLevels\":[]}}}";

        using var document = JsonDocument.Parse(payload);
        var traders = document.RootElement
            .GetProperty("data")
            .Deserialize<Dictionary<string, TarkovDev.Trader>>();

        Assert.NotNull(traders);
        if (traders is null)
        {
            return;
        }
        Assert.True(traders.ContainsKey("54cb50c76803fa8b248b4571"));
        Assert.Equal("prapor", traders["54cb50c76803fa8b248b4571"].normalizedName);
    }

    [Fact]
    public void RedactorTruncatesUntrustedExceptionText()
    {
        var sanitized = DiagnosticRedactor.Sanitize(new string('x', 200), 64);

        Assert.EndsWith("[TRUNCATED]", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length > 64);
    }
}
