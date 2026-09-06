using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiloviewPcOnboarding;

// A local process contract, never a network endpoint. The elevated server starts
// the installed utility with redirected pipes; remote approvals stay unchanged.
internal sealed record ServerOnboardingRequest(
    int SchemaVersion,
    string Operation,
    string? AdapterId = null,
    string? Address = null,
    string? JobName = null,
    string? NdiDiscoveryServerIp = null,
    bool AcceptLicense = false);

internal sealed record ServerOnboardingResponse(
    int SchemaVersion,
    bool Success,
    string Version,
    RegistrationRequest? Endpoint = null,
    string? Error = null);

internal static class ServerOnboardingCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        ServerOnboardingResponse response;
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            var buffer = new char[16 * 1024 + 1];
            var count = await input.ReadBlockAsync(buffer.AsMemory(), timeout.Token);
            if (count > 16 * 1024) throw new ArgumentException("Server command exceeds 16 KiB.");
            var request = JsonSerializer.Deserialize<ServerOnboardingRequest>(buffer.AsSpan(0, count), Json)
                ?? throw new ArgumentException("A server command is required.");
            Validate(request);
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("The server command must run with administrator rights.");

            if (request.Operation == "install")
            {
                if (!request.AcceptLicense)
                    throw new InvalidOperationException("The installer must include and accept the PC Agent license.");
                var installed = AgentInstallationService.InstallOrUpdate(AgentInstallationService.PreferredNetwork());
                if (!installed.Installed) throw new InvalidOperationException(installed.Message);
                ConsentStore.Record("1.0");
                response = new(1, true, NdiToolsService.UtilityVersion());
            }
            else
            {
                if (!AgentInstallationService.IsInstalledUtility(Environment.ProcessPath))
                    throw new InvalidOperationException("Local server onboarding requires the installed PC Agent Setup executable.");
                if (!ConsentStore.IsAccepted("1.0"))
                    throw new InvalidOperationException("Install the PC Agent component and accept its license first.");
                var network = ResolveNetwork(request, NetworkService.GetChoices());
                var endpointId = ConsentStore.EndpointId();
                var configuration = new RemoteOnboardingConfiguration(1, "NDI Job Configurator", endpointId,
                    request.JobName!, request.NdiDiscoveryServerIp!, null);
                RemoteOnboardingService.ValidateConfiguration(configuration, endpointId);
                await NdiConfigurationService.PreflightAsync(timeout.Token);
                var server = new JobConfiguratorInstance(network.Address, new Uri($"http://{network.Address}:8091"),
                    "local", "managed", configuration.JobName.Trim(), configuration.NdiDiscoveryServerIp, true);
                await NdiConfigurationService.ApplyAsync(network, server, timeout.Token);
                var installed = AgentInstallationService.InstallOrUpdate(network);
                if (!installed.Installed) throw new InvalidOperationException(installed.Message);
                AgentInstallationService.RecordMembership(network, server);
                var ndi = await new NdiToolsService().CheckAsync(timeout.Token);
                var endpoint = new RegistrationRequest(endpointId, Environment.MachineName, network.Address,
                    network.Name, network.PrefixLength, true, ndi.InstalledVersion?.ToString() ?? "not installed",
                    NdiToolsService.UtilityVersion(), "1.0", RuntimeInformation.OSDescription.Trim());
                response = new(1, true, NdiToolsService.UtilityVersion(), endpoint);
            }
        }
        catch (Exception ex)
        {
            response = new(1, false, NdiToolsService.UtilityVersion(), Error: ex.Message);
        }
        await using var output = Console.OpenStandardOutput();
        await JsonSerializer.SerializeAsync(output, response, Json);
        return response.Success ? 0 : 1;
    }

    internal static void Validate(ServerOnboardingRequest request)
    {
        if (request.SchemaVersion != 1 || request.Operation is not ("install" or "onboard"))
            throw new ArgumentException("Unsupported local server command.");
        if (request.Operation == "install") return;
        if (request.AcceptLicense) throw new ArgumentException("Onboarding cannot grant installer consent.");
        if (string.IsNullOrWhiteSpace(request.AdapterId) || !IPAddress.TryParse(request.Address, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            throw new ArgumentException("Select an active server IPv4 adapter.");
        RemoteOnboardingService.ValidateConfiguration(new(1, "NDI Job Configurator", Guid.Empty.ToString(),
            request.JobName!, request.NdiDiscoveryServerIp!, null), Guid.Empty.ToString());
    }

    internal static NetworkChoice ResolveNetwork(ServerOnboardingRequest request, IReadOnlyList<NetworkChoice> networks) =>
        networks.FirstOrDefault(network => string.Equals(network.Id, request.AdapterId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(network.Address, request.Address, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("The requested server adapter/address is no longer active.");
}
