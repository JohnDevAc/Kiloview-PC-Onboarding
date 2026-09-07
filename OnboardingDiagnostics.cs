using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NdiSuite.Onboarding;

internal sealed record FailureEntry(DateTimeOffset AtUtc, string Stage, string Message);
internal sealed record FailureReport(int SchemaVersion, string ReportId, string EndpointId, string AttemptId,
    DateTimeOffset OccurredUtc, string Version, string Stage, string ErrorType, string Message, string StackTrace,
    IReadOnlyList<FailureEntry> Entries);
internal sealed record DiagnosticDelivery(FailureReport Report, string ServerAddress, string AdapterId,
    DateTimeOffset QueuedUtc, DateTimeOffset NextAttemptUtc);
internal sealed record DiagnosticReceipt(string ReportId, DateTimeOffset ExpiresUtc);

internal sealed class OnboardingTrace
{
    private readonly List<FailureEntry> _entries = [];
    internal string CurrentStage { get; private set; } = "startup";
    internal void Step(string stage, string message)
    {
        CurrentStage = stage;
        if (_entries.Count == 32) _entries.RemoveAt(0);
        _entries.Add(new(DateTimeOffset.UtcNow, stage, message));
    }
    internal FailureReport Failure(string endpointId, string attemptId, string version, Exception exception) => OnboardingDiagnostics.Bound(new(
        1, Guid.NewGuid().ToString("D"), endpointId, attemptId, DateTimeOffset.UtcNow, version, CurrentStage,
        exception.GetType().Name, OnboardingDiagnostics.Redact(exception.Message),
        OnboardingDiagnostics.Redact(ExceptionDetails(exception), 6000), _entries.ToArray()));
    private static string ExceptionDetails(Exception exception) => string.Join(Environment.NewLine,
        Flatten(exception).Take(6).Select(e => e.GetType().FullName + ": " + e.Message + Environment.NewLine + new StackTrace(e, false)));
    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        yield return ex;
        IEnumerable<Exception> innerExceptions = ex is AggregateException aggregate ? aggregate.InnerExceptions : ex.InnerException is { } nested ? new[] { nested } : [];
        foreach (var inner in innerExceptions)
            foreach (var item in Flatten(inner)) yield return item;
    }
}

internal static class OnboardingDiagnostics
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumQueue = 64;
    private static string DirectoryPath(string statePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, "onboarding-diagnostics");
    private static string PathFor(string statePath, string reportId) => Path.Combine(DirectoryPath(statePath), Guid.Parse(reportId).ToString("D") + ".json");

    internal static void Queue(string statePath, FailureReport report, string serverAddress, string adapterId)
    {
        Validate(report, serverAddress, adapterId);
        Cleanup(statePath, DateTimeOffset.UtcNow);
        Write(statePath, new(report, serverAddress, adapterId, DateTimeOffset.UtcNow, DateTimeOffset.MinValue));
        Cleanup(statePath, DateTimeOffset.UtcNow);
    }

    internal static async Task<bool> TrySendAsync(string statePath, DiagnosticDelivery delivery, HttpClient client, CancellationToken ct)
    {
        try
        {
            Validate(delivery.Report, delivery.ServerAddress, delivery.AdapterId);
            using var response = await client.PostAsJsonAsync($"http://{delivery.ServerAddress}:8091/api/pc-onboarding/diagnostics", delivery.Report, Json, ct);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<DiagnosticReceipt>(Json, ct);
            if (receipt?.ReportId != delivery.Report.ReportId) throw new IOException("The diagnostic receipt did not match the failure report.");
            File.Delete(PathFor(statePath, delivery.Report.ReportId));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        {
            // Independent, bounded delivery attempts never replace the original onboarding error.
            try { Write(statePath, delivery with { NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(1) }); }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    internal static DiagnosticDelivery? Next(string statePath, string endpointId, string adapterId, DateTimeOffset now)
    {
        Cleanup(statePath, now);
        return ReadAll(statePath).Where(d => d.Report.EndpointId == endpointId && d.AdapterId == adapterId && d.NextAttemptUtc <= now)
            .OrderBy(d => d.NextAttemptUtc).ThenBy(d => d.QueuedUtc).FirstOrDefault();
    }

    internal static async Task CaptureRemoteAsync(string statePath, string endpointId, string? attemptId, string serverAddress,
        string adapterId, string? localAddress, string version, OnboardingTrace trace, Exception error)
    {
        if (!Guid.TryParse(attemptId, out var attempt) || attempt == Guid.Empty) return;
        try
        {
            var report = trace.Failure(endpointId, attemptId, version, error);
            Queue(statePath, report, serverAddress, adapterId);
            if (localAddress is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = OnboardingOutcomes.CreateClient(localAddress);
            await TrySendAsync(statePath, new(report, serverAddress, adapterId, DateTimeOffset.UtcNow, DateTimeOffset.MinValue), client, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { /* The original failure is still displayed locally if diagnostic storage is unavailable. */ }
    }

    internal static string Redact(string? text, int limit = 2048)
    {
        var value = text ?? "";
        if (value.Length > 32 * 1024) value = value[..(32 * 1024)];
        value = Regex.Replace(value, @"(?i)Bearer\s+[^\s""']+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)(password|passwd|authorization|access[_-]?token|refresh[_-]?token|secret)(\s*[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;}]+)", "$1$2[redacted]");
        value = Regex.Replace(value, @"(?i)Bearer\s+[^\s""']+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[redacted]@");
        value = Regex.Replace(value, @"(?i)[A-Z]:\\Users\\[^\\\s]+", @"%USERPROFILE%");
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    internal static FailureReport Bound(FailureReport report)
    {
        report = report with
        {
            Version = Redact(report.Version, 80),
            Stage = Redact(report.Stage, 80),
            ErrorType = Redact(report.ErrorType, 160),
            Entries = report.Entries.TakeLast(32).Select(e => e with { Stage = Redact(e.Stage, 80), Message = Redact(e.Message, 256) }).ToArray()
        };
        while (JsonSerializer.SerializeToUtf8Bytes(report, Json).Length > 16 * 1024)
            report = report with
            {
                Message = report.Message[..Math.Min(report.Message.Length, 1024)],
                StackTrace = report.StackTrace[..(report.StackTrace.Length / 2)],
                Entries = report.Entries.TakeLast(Math.Max(1, report.Entries.Count / 2)).ToArray()
            };
        return report;
    }

    private static void Validate(FailureReport? report, string address, string adapter)
    {
        if (report is null || report.Version is null || report.Stage is null || report.ErrorType is null || report.Message is null
            || report.StackTrace is null || report.Entries is null || report.Entries.Count > 32 || report.Entries.Any(e => e is null)
            || report.SchemaVersion != 1 || !Guid.TryParse(report.ReportId, out var id) || id == Guid.Empty
            || !Guid.TryParse(report.EndpointId, out id) || id == Guid.Empty
            || !Guid.TryParse(report.AttemptId, out id) || id == Guid.Empty || string.IsNullOrWhiteSpace(adapter)
            || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.GetAddressBytes()[0] >= 224
            || JsonSerializer.SerializeToUtf8Bytes(report, Json).Length > 32 * 1024)
            throw new IOException("Invalid onboarding diagnostic delivery.");
    }

    private static void Write(string statePath, DiagnosticDelivery delivery)
    {
        Directory.CreateDirectory(DirectoryPath(statePath));
        var path = PathFor(statePath, delivery.Report.ReportId);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, delivery, Json); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<DiagnosticDelivery> ReadAll(string statePath)
    {
        var directory = DirectoryPath(statePath);
        if (!Directory.Exists(directory)) return [];
        var values = new List<DiagnosticDelivery>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 40 * 1024) continue;
                var value = JsonSerializer.Deserialize<DiagnosticDelivery>(File.ReadAllText(path), Json);
                if (value is null) continue;
                Validate(value.Report, value.ServerAddress, value.AdapterId);
                if (PathFor(statePath, value.Report.ReportId).Equals(path, StringComparison.OrdinalIgnoreCase)) values.Add(value);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        return values;
    }

    internal static void Cleanup(string statePath, DateTimeOffset now)
    {
        var directory = DirectoryPath(statePath);
        if (!Directory.Exists(directory)) return;
        var expired = ReadAll(statePath).Where(d => d.QueuedUtc.AddDays(7) <= now).Select(d => PathFor(statePath, d.Report.ReportId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new DirectoryInfo(directory).GetFiles().OrderByDescending(f => f.CreationTimeUtc).ToArray();
        for (var index = 0; index < files.Length; index++)
            if (expired.Contains(files[index].FullName) || files[index].LastWriteTimeUtc < now.UtcDateTime.AddDays(-7) || index >= MaximumQueue)
                try { files[index].Delete(); } catch (IOException) { }
    }
}
