using System.Net;
using System.Text;
using NdiSuite.Onboarding;

internal static class DiagnosticsValidation
{
    internal static async Task RunAsync(string root)
    {
        var statePath = Path.Combine(root, "diagnostic-fixture", "agent-state.json");
        var endpoint = Guid.NewGuid().ToString();
        var adapter = Guid.NewGuid().ToString();
        var trace = new OnboardingTrace();
        trace.Step("fetch-configuration", "Fetching settings.");
        trace.Step("ndi-preflight", "Checking NDI clients.");
        var report = trace.Failure(endpoint, Guid.NewGuid().ToString(), "test", new IOException("password=hidden Close NDI client"));
        Check(!report.Message.Contains("hidden") && !report.StackTrace.Contains("hidden"), "Failure messages leaked credentials.");
        Check(report.Entries.Count == 2 && report.Stage == "ndi-preflight", "Failure stage/timeline were not captured.");
        OnboardingDiagnostics.Queue(statePath, report, "192.0.2.10", adapter);
        var delivery = OnboardingDiagnostics.Next(statePath, endpoint, adapter, DateTimeOffset.UtcNow)!;
        Check(delivery.Report == report || delivery.Report.ReportId == report.ReportId, "A queued diagnostic did not survive a file read.");
        using var offline = new HttpClient(new DiagnosticHandler(_ => throw new HttpRequestException("offline")));
        Check(!await OnboardingDiagnostics.TrySendAsync(statePath, delivery, offline, CancellationToken.None), "Offline delivery was reported successful.");
        Check(OnboardingDiagnostics.Next(statePath, endpoint, adapter, DateTimeOffset.UtcNow) is null, "Delivery did not back off.");
        var second = trace.Failure(endpoint, Guid.NewGuid().ToString(), "test", new IOException("Another failure"));
        OnboardingDiagnostics.Queue(statePath, second, "192.0.2.11", adapter);
        File.WriteAllText(Path.Combine(root, "diagnostic-fixture", "onboarding-diagnostics", "corrupt.json"), "{\"report\":null}");
        Check(OnboardingDiagnostics.Next(statePath, endpoint, adapter, DateTimeOffset.UtcNow)!.Report.ReportId == second.ReportId, "An offline server starved another failure report.");
        using var mismatched = new HttpClient(new DiagnosticHandler(_ => Receipt(Guid.NewGuid().ToString())));
        Check(!await OnboardingDiagnostics.TrySendAsync(statePath, delivery, mismatched, CancellationToken.None), "An unrelated receipt deleted queued evidence.");
        using var matching = new HttpClient(new DiagnosticHandler(request => {
            Check(request.RequestUri!.Host == "192.0.2.10" && request.RequestUri.Port == 8091 && request.RequestUri.AbsolutePath.EndsWith("/diagnostics"), "Diagnostic destination changed.");
            return Receipt(report.ReportId);
        }));
        Check(await OnboardingDiagnostics.TrySendAsync(statePath, delivery, matching, CancellationToken.None), "A matching server receipt was not accepted.");
        Check(!File.Exists(Path.Combine(root, "diagnostic-fixture", "onboarding-diagnostics", report.ReportId + ".json")), "Acknowledged report remained queued.");
        Check(OnboardingDiagnostics.Next(statePath, "another-endpoint", adapter, DateTimeOffset.UtcNow.AddMinutes(2)) is null, "A report crossed endpoint identities.");
        OnboardingDiagnostics.Cleanup(statePath, DateTimeOffset.UtcNow.AddDays(8));
        Check(OnboardingDiagnostics.Next(statePath, endpoint, adapter, DateTimeOffset.UtcNow.AddDays(8)) is null, "Expired diagnostics were retained.");
        Console.WriteLine("DIAGNOSTIC_QUEUE_RETRY_REDACTION_RETENTION=PASS");
    }
    private static HttpResponseMessage Receipt(string reportId) => new(HttpStatusCode.OK) {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { reportId, expiresUtc = DateTimeOffset.UtcNow.AddDays(7) }), Encoding.UTF8, "application/json")
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class DiagnosticHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
}
