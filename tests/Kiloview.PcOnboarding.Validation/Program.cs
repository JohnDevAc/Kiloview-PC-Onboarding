using KiloviewPcOnboarding;

var testRoot = Path.Combine(Path.GetTempPath(), $"Kiloview-Payload-{Guid.NewGuid():N}");
var packagedAgent = Path.Combine(testRoot, "Agent", "NDI Configurator PC Agent.exe");
var installedAgent = Path.Combine(testRoot, "NDI Configurator PC Agent.exe");

try
{
    Directory.CreateDirectory(Path.GetDirectoryName(packagedAgent)!);
    File.WriteAllText(packagedAgent, "packaged");
    File.WriteAllText(installedAgent, "installed");

    Require(
        AgentInstallationService.ResolveAgentPayload(testRoot) == Path.GetFullPath(packagedAgent),
        "Packaged layout did not prefer the Agent subdirectory payload.");

    File.Delete(packagedAgent);
    Require(
        AgentInstallationService.ResolveAgentPayload(testRoot) == Path.GetFullPath(installedAgent),
        "Installed layout did not resolve the sibling agent payload.");

    File.Delete(installedAgent);
    Require(
        AgentInstallationService.ResolveAgentPayload(testRoot) is null,
        "A missing agent payload was unexpectedly resolved.");

    Console.WriteLine("ONBOARDING_PACKAGED_AGENT_PAYLOAD=PASS");
    Console.WriteLine("ONBOARDING_INSTALLED_AGENT_PAYLOAD=PASS");

    var endpointId = Guid.NewGuid().ToString("D");
    var options = KiloviewPcOnboarding.Program.RemoteOptions(
    [
        "--remote-onboarding",
        "--configurator", "http://192.168.50.10:8091/",
        "--requesting-address", "192.168.50.10",
        "--endpoint-id", endpointId
    ]);
    Require(options is not null && options.EndpointId == endpointId, "Remote onboarding arguments were not parsed.");

    var configuration = new RemoteOnboardingConfiguration(
        1,
        "NDI Job Configurator",
        endpointId,
        "Remote test job",
        "192.168.50.11",
        new RemoteNetworkConfiguration(
            "adapter-id",
            "static",
            "192.168.50.20",
            24,
            "192.168.50.1",
            ["192.168.50.2", "192.168.50.3"]));
    RemoteOnboardingService.ValidateConfiguration(configuration, endpointId);
    RemoteOnboardingService.ValidateConfiguration(
        configuration with { Product = "Kiloview Job Configurator" },
        endpointId);
    RequireThrows(
        () => RemoteOnboardingService.ValidateConfiguration(
            configuration with { Product = "Unexpected Configurator" },
            endpointId),
        "An unrecognised remote configuration product identity was accepted.");
    var current = new NetworkChoice(
        "adapter-id",
        "Ethernet",
        "Test adapter",
        "192.168.50.19",
        24);
    var staticPlan = NetworkConfigurationService.CreatePlan(
        current,
        configuration.Network,
        "192.168.50.10");
    Require(
        staticPlan.ChangesNetwork
        && staticPlan.Address == "192.168.50.20"
        && staticPlan.PrefixLength == 24,
        "Static remote network configuration was not planned correctly.");
    var dhcpPlan = NetworkConfigurationService.CreatePlan(
        current,
        new RemoteNetworkConfiguration("adapter-id", "dhcp", null, null, null, null),
        "192.168.50.10");
    Require(dhcpPlan.ChangesNetwork && dhcpPlan.Mode == "dhcp", "DHCP mode was not accepted.");
    RequireThrows(
        () => RemoteOnboardingService.ValidateConfiguration(
            configuration with { EndpointId = Guid.NewGuid().ToString("D") },
            endpointId),
        "A mismatched endpoint identity was accepted.");
    RequireThrows(
        () => NetworkConfigurationService.CreatePlan(
            current,
            configuration.Network! with { Address = "192.168.51.20" },
            "192.168.50.10"),
        "An off-subnet static address was accepted.");
    Require(
        RemoteOnboardingService.NeedsNdiAttention(
            new NdiToolsStatus(false, null, new Version(6, 3), null, "NDI Tools is not installed.")),
        "Missing NDI Tools did not require a final user warning.");
    Require(
        RemoteOnboardingService.NeedsNdiAttention(
            new NdiToolsStatus(true, new Version(6, 2), new Version(6, 3), null, "Update required.")),
        "Outdated NDI Tools did not require a final user warning.");
    Require(
        RemoteOnboardingService.NeedsNdiAttention(
            new NdiToolsStatus(true, new Version(6, 3), null, null, "Currency unknown.")),
        "Unconfirmed NDI Tools currency did not require a final user warning.");
    Require(
        !RemoteOnboardingService.NeedsNdiAttention(
            new NdiToolsStatus(true, new Version(6, 3), new Version(6, 3), null, "Current.")),
        "Current NDI Tools incorrectly required a final warning.");
    Require(
        NdiConfigurationService.IsBlockingDiscoveryProcessName(
            "Application.NDI.DiscoveryService.UI"),
        "The interactive NDI Discovery settings process was not identified as blocking.");
    Require(
        !NdiConfigurationService.IsBlockingDiscoveryProcessName("NDI Discovery Service"),
        "The always-on NDI Discovery background service was incorrectly identified as blocking.");

    Console.WriteLine("REMOTE_ONBOARDING_ARGUMENTS=PASS");
    Console.WriteLine("REMOTE_ONBOARDING_CONFIGURATION_VALIDATION=PASS");
    Console.WriteLine("REMOTE_NETWORK_STATIC_PLAN=PASS");
    Console.WriteLine("REMOTE_NETWORK_DHCP_PLAN=PASS");
    Console.WriteLine("REMOTE_NDI_FINAL_NOTIFICATION=PASS");
    Console.WriteLine("REMOTE_NDI_DISCOVERY_PREFLIGHT=PASS");
}
finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RequireThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}
