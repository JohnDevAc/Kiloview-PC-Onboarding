using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using NdiSuite.Onboarding;

namespace KiloviewPcOnboarding;

internal static class RemoteOnboardingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedConfiguratorProducts =
    [
        "NDI Job Configurator",
        "Kiloview Job Configurator"
    ];

    public static async Task<RemoteOnboardingResult> ExecuteAsync(
        RemoteOnboardingOptions options,
        CancellationToken ct)
    {
        ValidateOptions(options);
        var current = AgentInstallationService.PreferredNetwork()
            ?? throw new InvalidOperationException(
                "NDI Configurator PC Agent does not have a selected production adapter. Reinstall the agent locally first.");
        var configuration = await FetchConfigurationAsync(current, options, ct);
        ValidateConfiguration(configuration, options.EndpointId);
        if (!configuration.RequiresFinalConfirmation || !Guid.TryParse(options.AttemptId, out _) || configuration.AttemptId != options.AttemptId
            || string.IsNullOrWhiteSpace(configuration.JobId) || string.IsNullOrWhiteSpace(configuration.JobRevision))
            throw new InvalidOperationException("Update Job Configurator and request a new approved onboarding attempt before configuring this PC.");
        // UAC and local approval happen before this process fetches settings.
        // Leave a minute of the server's five-minute window for outcome delivery.
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execution.CancelAfter(TimeSpan.FromMinutes(4));
        ct = execution.Token;
        var plan = NetworkConfigurationService.CreatePlan(
            current,
            configuration.Network,
            options.RequestingAddress);

        await NdiConfigurationService.PreflightAsync(ct);
        var ndi = await new NdiToolsService().CheckAsync(ct);
        await using var mutationLock = await NdiConfigurationService.AcquireConfigurationLockAsync(ct);
        var recovery = NetworkConfigurationService.Capture(plan);
        var files = ConfigurationTransaction.CaptureFiles(NdiConfigurationService.ConfigurationPaths.Append(AgentInstallationService.ConfigurationPath));
        var outcome = new PendingOutcome(options.EndpointId, configuration.AttemptId!, configuration.JobId!, configuration.JobRevision!,
            options.RequestingAddress, current.Id, "applying", DateTimeOffset.UtcNow);
        OnboardingOutcomes.Write(AgentInstallationService.ConfigurationPath, outcome);
        var result = await ConfigurationTransaction.RunAsync(async () =>
        {
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
        NdiConfigurationService.EnsureApplicationsClosed();
        await NdiConfigurationService.ApplyConfigurationFilesAsync(network, server, ct,
            AgentInstallationService.PreviousJob(server.Address));

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
            CurrentWindowsVersion(), configuration.AttemptId, configuration.JobId, configuration.JobRevision);
        AgentInstallationService.RecordMembership(network, server);
        await JobConfiguratorDiscovery.RegisterAsync(network, server, request, ct);

        var ndiAttentionRequired = NeedsNdiAttention(ndi);
        return new RemoteOnboardingResult(
            server.JobName,
            network.Address,
            network.PrefixLength,
            plan.ChangesNetwork,
            ndiAttentionRequired,
            ndi.Message);
        }, async recoveryToken =>
        {
            var failures = new List<Exception>();
            if (plan.ChangesNetwork)
                try { await NetworkConfigurationService.RestoreAsync(recovery, options.RequestingAddress, recoveryToken); }
                catch (Exception ex) { failures.Add(ex); }
            try { ConfigurationTransaction.RestoreFiles(files); }
            catch (Exception ex) { failures.Add(ex); }
            outcome = outcome with { Outcome = failures.Count == 0 ? "aborted" : "recovery-required", UpdatedUtc = DateTimeOffset.UtcNow };
            OnboardingOutcomes.Write(AgentInstallationService.ConfigurationPath, outcome);
            await TryReportOutcomeAsync(outcome, recoveryToken);
            if (failures.Count > 0) throw new AggregateException(failures);
        });
        // Once the local transaction succeeded, a lost final acknowledgement must
        // never undo it. Agent retries this durable outcome after Setup exits/restarts.
        outcome = outcome with { Outcome = "completed", UpdatedUtc = DateTimeOffset.UtcNow };
        OnboardingOutcomes.Write(AgentInstallationService.ConfigurationPath, outcome);
        var confirmed = await TryReportOutcomeAsync(outcome, ct);
        return result with { ConfirmationPending = !confirmed };
    }

    private static async Task<bool> TryReportOutcomeAsync(PendingOutcome outcome, CancellationToken ct)
    {
        try
        {
            var network = AgentInstallationService.PreferredNetwork();
            if (network is null || network.Id != outcome.AdapterId) return false;
            using var client = OnboardingOutcomes.CreateClient(network.Address);
            var status = await OnboardingOutcomes.SendAsync(client, outcome, ct);
            OnboardingOutcomes.Acknowledge(AgentInstallationService.ConfigurationPath, outcome);
            if (outcome.Outcome == "completed" && status != "completed")
                throw new InvalidOperationException("The job or onboarding attempt changed before confirmation. Reapply approved onboarding for the current job.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException) { return false; }
    }

    internal static void ValidateConfiguration(
        RemoteOnboardingConfiguration configuration,
        string expectedEndpointId)
    {
        if (configuration.SchemaVersion != 1)
            throw new InvalidOperationException(
                $"Remote onboarding schema {configuration.SchemaVersion} is not supported.");
        if (configuration.Product is null
            || !SupportedConfiguratorProducts.Contains(configuration.Product))
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
        var path = $"/api/pc-onboarding/configuration/{Uri.EscapeDataString(options.EndpointId)}?attemptId={Uri.EscapeDataString(options.AttemptId ?? "")}";
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(new Uri(options.ConfiguratorBaseUri, path), ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Timed out waiting for NDI Job Configurator to return this PC's onboarding settings. "
                + "Confirm the server remains responsive after it accepts the onboarding request, then retry.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                "NDI Job Configurator could not be reached while this PC requested its onboarding settings.",
                ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    ErrorMessage(body)
                    ?? $"NDI Job Configurator returned {(int)response.StatusCode} while settings were requested.");
            }
            try
            {
                return await response.Content.ReadFromJsonAsync<RemoteOnboardingConfiguration>(Json, ct)
                    ?? throw new InvalidOperationException("NDI Job Configurator returned an empty configuration.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "NDI Job Configurator returned invalid remote onboarding JSON.",
                    ex);
            }
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
            throw new InvalidOperationException("The NDI Configurator PC Agent endpoint identity is invalid.");
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
