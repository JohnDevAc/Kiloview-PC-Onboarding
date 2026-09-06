using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace NdiSuite.Onboarding;

internal sealed record PendingOutcome(
    string EndpointId, string AttemptId, string JobId, string JobRevision,
    string ServerAddress, string AdapterId, string Outcome, DateTimeOffset UpdatedUtc);
internal sealed record OutcomeResult(string AttemptId, string JobId, string JobRevision, string Status);

internal static class OnboardingOutcomes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string DirectoryPath(string statePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, "onboarding-outcomes");
    private static string PathFor(string statePath, string attemptId) => Path.Combine(DirectoryPath(statePath), Guid.Parse(attemptId).ToString("D") + ".json");

    internal static void Write(string statePath, PendingOutcome outcome)
    {
        Validate(outcome);
        Directory.CreateDirectory(DirectoryPath(statePath));
        var path = PathFor(statePath, outcome.AttemptId);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, outcome, Json);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static IReadOnlyList<PendingOutcome> ReadAll(string statePath)
    {
        var directory = DirectoryPath(statePath);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            var value = JsonSerializer.Deserialize<PendingOutcome>(File.ReadAllText(path), Json)
                ?? throw new IOException("The pending onboarding outcome is invalid.");
            Validate(value);
            if (!string.Equals(path, PathFor(statePath, value.AttemptId), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The pending outcome filename does not match its attempt.");
            return value;
        }).OrderBy(value => value.UpdatedUtc).ToArray();
    }

    internal static void Acknowledge(string statePath, PendingOutcome expected)
    {
        var path = PathFor(statePath, expected.AttemptId);
        if (!File.Exists(path)) return;
        var current = JsonSerializer.Deserialize<PendingOutcome>(File.ReadAllText(path), Json);
        if (current == expected) File.Delete(path);
    }

    internal static HttpClient CreateClient(string localAddress)
    {
        var local = IPAddress.Parse(localAddress);
        var handler = new SocketsHttpHandler {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, ct) => {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try {
                    socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    internal static async Task<string> SendAsync(HttpClient client, PendingOutcome outcome, CancellationToken ct)
    {
        Validate(outcome);
        if (outcome.Outcome == "applying") throw new InvalidOperationException("An applying operation has no final outcome yet.");
        using var response = await client.PostAsJsonAsync($"http://{outcome.ServerAddress}:8091/api/pc-onboarding/outcome",
            new { outcome.EndpointId, outcome.AttemptId, outcome.JobId, outcome.JobRevision, outcome.Outcome }, Json, ct);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<OutcomeResult>(Json, ct)
            ?? throw new IOException("The server returned no onboarding outcome receipt.");
        if (receipt.AttemptId != outcome.AttemptId || receipt.JobId != outcome.JobId || receipt.JobRevision != outcome.JobRevision
            || receipt.Status is not ("completed" or "aborted" or "recovery-required" or "superseded"))
            throw new IOException("The server returned an unrelated onboarding outcome receipt.");
        return receipt.Status;
    }

    private static void Validate(PendingOutcome value)
    {
        if (!Guid.TryParse(value.EndpointId, out var endpoint) || endpoint == Guid.Empty
            || !Guid.TryParse(value.AttemptId, out var attempt) || attempt == Guid.Empty
            || string.IsNullOrWhiteSpace(value.JobId) || string.IsNullOrWhiteSpace(value.JobRevision)
            || string.IsNullOrWhiteSpace(value.AdapterId)
            || !IPAddress.TryParse(value.ServerAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)
            || value.Outcome is not ("applying" or "completed" or "aborted" or "recovery-required"))
            throw new IOException("The pending onboarding outcome is invalid. Reapply approved onboarding to repair it.");
    }
}
