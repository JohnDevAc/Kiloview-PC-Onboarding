using KiloviewPcOnboarding;

var testRoot = Path.Combine(Path.GetTempPath(), $"Kiloview-Payload-{Guid.NewGuid():N}");
var packagedAgent = Path.Combine(testRoot, "Agent", "NDI Configurator PC Agent.exe");
var installedAgent = Path.Combine(testRoot, "NDI Configurator PC Agent.exe");

try
{
    Directory.CreateDirectory(Path.GetDirectoryName(packagedAgent)!);
    Require(PackageInstallation.Compare("0.7.0-dev.2", "0.7.0") < 0
        && PackageInstallation.Compare("0.7.0-dev.10", "0.7.0-dev.2") > 0
        && PackageInstallation.Compare("0.7.0.0+build", "0.7.0") == 0, "Package version ordering is inconsistent.");
    Require(PackageInstallation.Retain("0.8.0", "0.8.0", "0.7.0"), "Matching newer installation must be retained.");
    foreach (var pair in new (string?, string?)[] { ("0.8.0", "0.7.0"), ("0.7.0", "0.8.0"), ("0.8.0", null), (null, "0.8.0") })
    {
        try { PackageInstallation.Retain(pair.Item1, pair.Item2, "0.7.0"); throw new Exception("Mixed newer installation was accepted."); }
        catch (InvalidOperationException) { }
    }
    var destinations = new[] { Path.Combine(testRoot, "installed-a"), Path.Combine(testRoot, "installed-b") };
    var sources = new[] { Path.Combine(testRoot, "source-a"), Path.Combine(testRoot, "source-b") };
    for (var i = 0; i < 2; i++) { File.WriteAllText(destinations[i], "old"); File.WriteAllText(sources[i], "new"); }
    var replacements = destinations.Zip(sources).ToDictionary(pair => pair.First, pair => pair.Second);
    var moved = 0;
    try { PackageInstallation.Replace(replacements, () => { }, (source, target) => { if (++moved == 2) throw new IOException("fixture lock"); File.Move(source, target, true); }); }
    catch (IOException) { }
    Require(destinations.All(path => File.ReadAllText(path) == "old"), "Failed second replacement must restore both files.");
    PackageInstallation.Replace(replacements, () => { });
    Require(destinations.All(path => File.ReadAllText(path) == "new"), "Complete pair did not replace together.");
    var interrupted = Path.Combine(testRoot, ".pc-agent-install-recovery");
    Directory.CreateDirectory(interrupted);
    File.WriteAllText(Path.Combine(interrupted, "installed-a"), "before interrupted update");
    File.WriteAllText(Path.Combine(interrupted, "manifest.json"), """{"installed-a":true}""");
    PackageInstallation.Recover(testRoot);
    Require(File.ReadAllText(destinations[0]) == "before interrupted update" && !Directory.Exists(interrupted), "Next Setup did not recover an interrupted replacement.");
    Console.WriteLine("COMPLETE_PACKAGE_RETENTION_AND_RECOVERY=PASS");
    var recoveryFile = Path.Combine(testRoot, "recovery-fixture.json");
    File.WriteAllText(recoveryFile, "original");
    var recoveryFiles = ConfigurationTransaction.CaptureFiles([recoveryFile]);
    try
    {
        await ConfigurationTransaction.RunAsync<int>(() =>
        {
            File.WriteAllText(recoveryFile, "partial change");
            throw new IOException("Simulated registration failure");
        }, token =>
        {
            Require(!token.IsCancellationRequested, "Recovery must get its own live deadline.");
            ConfigurationTransaction.RestoreFiles(recoveryFiles);
            return Task.CompletedTask;
        });
        throw new Exception("Failure was hidden.");
    }
    catch (InvalidOperationException ex) { Require(ex.Message.Contains("restored"), "Recovery outcome was missing."); }
    Require(File.ReadAllText(recoveryFile) == "original", "Failed onboarding did not restore fixture state.");
    Require(NetworkService.ScanAddresses(new("fixture", "Fixture", "Fixture", "192.0.2.10", 23)).Count() == 510, "Agent discovery truncated /23.");
    Console.WriteLine("REMOTE_TRANSACTION_RECOVERY=PASS");
    Console.WriteLine("FULL_SELECTED_SUBNET_DISCOVERY=PASS");
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
        "--endpoint-id", endpointId,
        "--attempt-id", "cb5311ad-6633-4f2f-8184-c5d081df27ed"
    ]);
    Require(options is not null && options.EndpointId == endpointId, "Remote onboarding arguments were not parsed.");
    Require(options!.AttemptId == "cb5311ad-6633-4f2f-8184-c5d081df27ed", "Remote command line lost the approval attempt.");

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

    var localCommand = new ServerOnboardingRequest(1, "onboard", current.Id, current.Address,
        "Local job", "192.168.50.11");
    ServerOnboardingCommand.Validate(localCommand);
    Require(ServerOnboardingCommand.ResolveNetwork(localCommand, [current]) == current,
        "Local server commands must use the selected active local adapter.");
    RequireThrows(() => ServerOnboardingCommand.ResolveNetwork(localCommand with { Address = "192.168.50.20" }, [current]),
        "A remote or stale address was accepted as the server adapter.");
    try
    {
        ServerOnboardingCommand.Validate(localCommand with { AcceptLicense = true });
        throw new InvalidOperationException("An onboarding command could grant installer consent.");
    }
    catch (ArgumentException) { }
    Require(!AgentInstallationService.IsInstalledUtility(Path.Combine(testRoot, "NDI Configurator PC Agent Setup.exe")),
        "A workspace copy was accepted as the installed no-prompt utility.");
    Require(NdiConfigurationService.ReplaceManagedGroup("Public,OldJob,Custom", "OldJob", "NextJob") == "Public,Custom,NextJob",
        "Replacing a managed job must retain unrelated groups and remove the previous job.");
    Require(NdiConfigurationService.ReplaceManagedGroup("Public,NextJob", "OldJob", "NextJob") == "Public,NextJob",
        "Managed job reapplication must be idempotent.");
    Console.WriteLine("LOCAL_SERVER_COMMAND_BOUNDARY=PASS");
    Console.WriteLine("LOCAL_SERVER_INSTALLED_UTILITY=PASS");
    Console.WriteLine("NDI_MANAGED_GROUP_REPLACEMENT=PASS");
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
