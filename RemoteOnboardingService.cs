using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KiloviewPcOnboarding;

internal static class RemoteOnboardingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<RemoteOnboardingResult> ExecuteAsync(
        RemoteOnboardingOptions options,
        CancellationToken ct)
    {
        ValidateOptions(options);
        var current = AgentInstallationService.PreferredNetwork()
            ?? throw new InvalidOperationException(
                "The PC Agent does not have a selected production adapter. Reinstall the agent locally first.");
        var configuration = await FetchConfigurationAsync(current, options, ct);
        ValidateConfiguration(configuration, options.EndpointId);
        var plan = NetworkConfigurationService.CreatePlan(
            current,
            configuration.Network,
            options.RequestingAddress);

        await NdiConfigurationService.PreflightAsync(ct);
        var ndi = await new NdiToolsService().CheckAsync(ct);
        var network = await NetworkConfigurationService.ApplyAsync(
            plan,
            options.RequestingAddress,
            ct);
        var server = new JobConfiguratorInstance(
            options.RequestingAddress,
            options.ConfiguratorBaseUri,
            "remote",
            "managed",
            configuration.JobName.Trim(),
            configuration.NdiDiscoveryServerIp,
            true);
        await NdiConfigurationService.ApplyAsync(network, server, ct);

        var installed = AgentInstallationService.InstallOrUpdate(network);
        if (!installed.Installed)
            throw new InvalidOperationException(installed.Message);
        var request = new RegistrationRequest(
            options.EndpointId,
            Environment.MachineName,
            network.Address,
            network.Name,
            network.PrefixLength,
            true,
            ndi.InstalledVersion?.ToString() ?? "not installed",
            NdiToolsService.UtilityVersion(),
            "1.0",
            CurrentWindowsVersion());
        await JobConfiguratorDiscovery.RegisterAsync(network, server, request, ct);
        AgentInstallationService.RecordMembership(network, server);

        var ndiAttentionRequired = NeedsNdiAttention(ndi);
        return new(
            server.JobName,
            network.Address,
            network.PrefixLength,
            plan.ChangesNetwork,
            ndiAttentionRequired,
            ndi.Message);
    }

    internal static void ValidateConfiguration(
        RemoteOnboardingConfiguration configuration,
        string expectedEndpointId)
    {
        if (configuration.SchemaVersion != 1)
            throw new InvalidOperationException(
                $"Remote onboarding schema {configuration.SchemaVersion} is not supported.");
        if (!string.Equals(
                configuration.Product,
                "Kiloview Job Configurator",
                StringComparison.Ordinal))
            throw new InvalidOperationException("The remote configuration product identity is invalid.");
        if (!Guid.TryParse(configuration.EndpointId, out var returnedEndpoint)
            || !Guid.TryParse(expectedEndpointId, out var expectedEndpoint)
            || returnedEndpoint != expectedEndpoint)
            throw new InvalidOperationException(
                "The remote configuration does not match this PC's endpoint identity.");
        var jobName = configuration.JobName?.Trim();
        if (string.IsNullOrWhiteSpace(jobName)
            || jobName.Length > 128
            || jobName.Any(char.IsControl))
            throw new InvalidOperationException("The remote job name is invalid.");
        if (!IPAddress.TryParse(configuration.NdiDiscoveryServerIp, out var discovery)
            || discovery.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || discovery.Equals(IPAddress.Any)
            || discovery.Equals(IPAddress.Broadcast)
            || IPAddress.IsLoopback(discovery))
            throw new InvalidOperationException("The remote NDI discovery server is not valid IPv4.");
    }

    internal static bool NeedsNdiAttention(NdiToolsStatus status) =>
        status.UpdateRequired || status.CurrentVersion is null;

    private static async Task<RemoteOnboardingConfiguration> FetchConfigurationAsync(
        NetworkChoice current,
        RemoteOnboardingOptions options,
        CancellationToken ct)
    {
        using var client = NetworkService.CreateBoundClient(current, TimeSpan.FromSeconds(10));
        var path = $"/api/pc-onboarding/configuration/{Uri.EscapeDataString(options.EndpointId)}";
        using var response = await client.GetAsync(new Uri(options.ConfiguratorBaseUri, path), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                ErrorMessage(body)
                ?? $"Job Configurator returned {(int)response.StatusCode} while settings were requested.");
        }
        try
        {
            return await response.Content.ReadFromJsonAsync<RemoteOnboardingConfiguration>(Json, ct)
                ?? throw new InvalidOperationException("Job Configurator returned an empty configuration.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Job Configurator returned invalid remote onboarding JSON.",
                ex);
        }
    }

    private static void ValidateOptions(RemoteOnboardingOptions options)
    {
        if (options.ConfiguratorBaseUri.Scheme != Uri.UriSchemeHttp
            || options.ConfiguratorBaseUri.Port != 8091
            || !IPAddress.TryParse(options.RequestingAddress, out var requesting)
            || requesting.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !string.Equals(
                options.ConfiguratorBaseUri.Host,
                requesting.ToString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Remote onboarding must use the requesting Configurator's IPv4 address on TCP 8091.");
        if (!Guid.TryParse(options.EndpointId, out _))
            throw new InvalidOperationException("The PC Agent endpoint identity is invalid.");
    }

    private static string? ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var value = error.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Length <= 512 ? value : value[..512];
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string CurrentWindowsVersion()
    {
        var description = RuntimeInformation.OSDescription.Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = Environment.OSVersion.VersionString;
        return description.Length <= 128 ? description : description[..128];
    }
}
