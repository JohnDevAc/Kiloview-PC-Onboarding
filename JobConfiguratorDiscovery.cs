using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace KiloviewPcOnboarding;

internal static class JobConfiguratorDiscovery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<JobConfiguratorInstance>> FindAsync(
        NetworkChoice network,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var addresses = NetworkService.ScanAddresses(network).ToArray();
        var found = new List<JobConfiguratorInstance>();
        var gate = new object();
        var completed = 0;
        using var client = NetworkService.CreateBoundClient(network, TimeSpan.FromMilliseconds(1400));
        await Parallel.ForEachAsync(
            addresses,
            new ParallelOptions { MaxDegreeOfParallelism = 48, CancellationToken = ct },
            async (address, token) =>
            {
                try
                {
                    var item = await ProbeAsync(client, address, token);
                    if (item is not null)
                    {
                        lock (gate) found.Add(item);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or TaskCanceledException
                    or JsonException)
                {
                    // Most addresses do not host the application.
                }
                finally
                {
                    var count = Interlocked.Increment(ref completed);
                    progress?.Report(count * 100 / Math.Max(1, addresses.Length));
                }
            });
        return found.OrderBy(item => item.JobName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task RegisterAsync(
        NetworkChoice network,
        JobConfiguratorInstance server,
        RegistrationRequest request,
        CancellationToken ct)
    {
        using var client = NetworkService.CreateBoundClient(network, TimeSpan.FromSeconds(10));
        using var response = await client.PostAsJsonAsync(
            new Uri(server.BaseUri, "/api/pc-onboarding/register"),
            request,
            Json,
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return;
        try
        {
            using var error = JsonDocument.Parse(body);
            if (error.RootElement.TryGetProperty("error", out var message))
                throw new InvalidOperationException(message.GetString() ?? $"Registration failed ({(int)response.StatusCode}).");
        }
        catch (JsonException) { }
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(body) ? $"Registration failed ({(int)response.StatusCode})." : body);
    }

    public static async Task<IReadOnlySet<string>> FindExistingRegistrationsAsync(
        NetworkChoice network,
        IReadOnlyList<JobConfiguratorInstance> servers,
        string endpointId,
        string hostname,
        CancellationToken ct)
    {
        var registeredServerAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        using var client = NetworkService.CreateBoundClient(network, TimeSpan.FromSeconds(3));
        await Parallel.ForEachAsync(
            servers.Where(server => server.SupportsRegistration),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (server, token) =>
            {
                try
                {
                    using var response = await client.GetAsync(
                        new Uri(server.BaseUri, "api/state"),
                        HttpCompletionOption.ResponseHeadersRead,
                        token);
                    if (!response.IsSuccessStatusCode || !IsJson(response))
                        return;

                    var state = await response.Content.ReadFromJsonAsync<StateResponse>(Json, token);
                    var match = state?.RemoteWindowsPcs?.Any(endpoint =>
                        string.Equals(endpoint.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(endpoint.Address, network.Address, StringComparison.Ordinal)
                        || string.Equals(endpoint.Hostname, hostname, StringComparison.OrdinalIgnoreCase)) == true;
                    if (match)
                    {
                        lock (gate)
                            registeredServerAddresses.Add(server.Address);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or TaskCanceledException
                    or JsonException)
                {
                    // Availability notification is best-effort; discovery remains usable.
                }
            });
        return registeredServerAddresses;
    }

    private static async Task<JobConfiguratorInstance?> ProbeAsync(
        HttpClient client,
        IPAddress address,
        CancellationToken ct)
    {
        var baseUri = new Uri($"http://{address}:8091/");
        using var healthResponse = await client.GetAsync(
            new Uri(baseUri, "api/health"),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!healthResponse.IsSuccessStatusCode) return null;
        var health = await healthResponse.Content.ReadFromJsonAsync<HealthResponse>(Json, ct);
        if (health is null
            || !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || health.Product is not null
            && !string.Equals(health.Product, "Kiloview Job Configurator", StringComparison.Ordinal))
            return null;

        using var profileResponse = await client.GetAsync(
            new Uri(baseUri, "api/pc-onboarding/profile"),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (profileResponse.IsSuccessStatusCode && IsJson(profileResponse))
        {
            var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(Json, ct);
            if (profile is null || string.IsNullOrWhiteSpace(profile.JobName)
                || string.IsNullOrWhiteSpace(profile.NdiDiscoveryServerIp))
                return null;
            return new(
                address.ToString(),
                baseUri,
                health.Version ?? profile.Version ?? "unknown",
                health.Channel ?? profile.Channel ?? "unknown",
                profile.JobName,
                profile.NdiDiscoveryServerIp,
                true);
        }

        // Older Configurators route the unknown profile URL to index.html.
        // Their existing state endpoint is enough to discover and identify them.
        using var stateResponse = await client.GetAsync(
            new Uri(baseUri, "api/state"),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!stateResponse.IsSuccessStatusCode || !IsJson(stateResponse)) return null;
        var state = await stateResponse.Content.ReadFromJsonAsync<StateResponse>(Json, ct);
        if (state?.LastJob is null
            || string.IsNullOrWhiteSpace(state.LastJob.JobName)
            || string.IsNullOrWhiteSpace(state.LastJob.NdiDiscoveryServerIp))
            return null;
        return new(
            address.ToString(),
            baseUri,
            health.Version ?? "unknown",
            health.Channel ?? "unknown",
            state.LastJob.JobName,
            state.LastJob.NdiDiscoveryServerIp,
            false);
    }

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType?.EndsWith(
            "/json",
            StringComparison.OrdinalIgnoreCase) == true
        || response.Content.Headers.ContentType?.MediaType?.EndsWith(
            "+json",
            StringComparison.OrdinalIgnoreCase) == true;

    private sealed record HealthResponse(string Status, string? Product, string? Version, string? Channel);
    private sealed record ProfileResponse(
        string Product,
        string? Version,
        string? Channel,
        string JobName,
        string NdiDiscoveryServerIp);
    private sealed record StateResponse(
        LegacyJob? LastJob,
        IReadOnlyList<RemoteWindowsPcResponse>? RemoteWindowsPcs);
    private sealed record LegacyJob(string JobName, string NdiDiscoveryServerIp);
    private sealed record RemoteWindowsPcResponse(
        string EndpointId,
        string Hostname,
        string Address,
        string Status);
}
