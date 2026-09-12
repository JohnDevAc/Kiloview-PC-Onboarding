using System.Text.Json;
using KiloviewPcOnboarding;

internal static class UpgradeValidation
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "upgrade-preflight");
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "agent-state.json");
        var legacyPath = Path.Combine(directory, "legacy-state.json");
        var adapter = Guid.NewGuid().ToString("B");
        var endpoint = Guid.NewGuid().ToString("D");
        var original = JsonSerializer.Serialize(new { schemaVersion = 1, endpointId = endpoint, adapterId = adapter,
            adapterName = "Ethernet", address = "192.0.2.15", prefixLength = 24,
            installedUtc = DateTimeOffset.UtcNow, updatedUtc = DateTimeOffset.UtcNow,
            memberships = new[] { new { serverAddress = "192.0.2.10", baseUri = "http://192.0.2.10:8091/", jobName = "Preserved job", registeredUtc = DateTimeOffset.UtcNow } } });
        File.WriteAllText(statePath, original);
        var installCalls = 0;
        AgentInstallationService.InstallationPlan? lastPlan = null;
        AgentInstallationResult InstallFixture(AgentInstallationService.InstallationPlan plan)
        {
            installCalls++;
            lastPlan = plan;
            // Only temporary files stand in for installation and configuration mutations.
            AgentInstallationService.ApplyInstallationConfiguration(plan,
                network => File.WriteAllText(statePath, JsonSerializer.Serialize(new { address = network.Address })));
            File.WriteAllText(Path.Combine(directory, "package-installed"), "fixture");
            return new(true, plan.Network is not null, "fixture");
        }
        var apipa = new NetworkChoice(adapter, "Ethernet", "Fixture", "169.254.183.231", 16);
        var wifi = new NetworkChoice(Guid.NewGuid().ToString("B"), "Wi-Fi", "Fixture", "192.0.2.105", 24);
        var result = AgentInstallationService.UpgradePackage(statePath, legacyPath, [apipa, wifi], InstallFixture);
        Check(result.Installed && installCalls == 1 && File.ReadAllText(statePath) == original,
            "Upgrade replaced the saved adapter/address with APIPA or changed saved membership state.");
        Check(lastPlan is { PreserveConfiguration: true, Network.Address: "192.0.2.15" }
            && lastPlan.Network.Id == adapter && lastPlan.Network.PrefixLength == 24, "Upgrade lost the saved network identity.");
        Console.WriteLine("UPGRADE_APIPA_PRESERVES_SAVED_CONFIGURATION=PASS");

        foreach (var current in new[] { apipa with { Address = "192.0.2.15", PrefixLength = 24 }, apipa with { Address = "192.0.2.16", PrefixLength = 24 } })
        {
            AgentInstallationService.UpgradePackage(statePath, legacyPath, [current, wifi], InstallFixture);
            Check(File.ReadAllText(statePath) == original && lastPlan!.Network!.Address == "192.0.2.15",
                "An ordinary software upgrade changed saved state or silently selected another adapter/address.");
        }
        Console.WriteLine("UPGRADE_VALID_AND_CHANGED_DHCP_PRESERVE_STATE=PASS");

        void Reject(Action action)
        {
            var calls = installCalls;
            var before = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
            try { action(); throw new Exception("Unsafe upgrade preflight succeeded."); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException) { }
            Check(installCalls == calls, "Rejected preflight reached package/startup/firewall/process mutations.");
            Check(before is null ? !File.Exists(statePath) : File.ReadAllBytes(statePath).SequenceEqual(before), "Rejected preflight changed saved state.");
        }
        Reject(() => AgentInstallationService.UpgradePackage(statePath, legacyPath, [wifi], InstallFixture));
        Reject(() => AgentInstallationService.UpgradePackage(statePath, legacyPath, [], InstallFixture));
        Reject(() => AgentInstallationService.UpgradePackage(statePath, legacyPath, [apipa, apipa with { Name = "Different interface" }], InstallFixture));
        foreach (var invalid in new[] { "{invalid", "null", original.Replace("192.0.2.15", "169.254.183.231"), original.Replace("\"memberships\":[", "\"memberships\":[null,") })
        {
            File.WriteAllText(statePath, invalid);
            Reject(() => AgentInstallationService.UpgradePackage(statePath, legacyPath, [apipa], InstallFixture));
        }
        File.WriteAllText(statePath, original);
        using (var locked = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var calls = installCalls;
            try { AgentInstallationService.UpgradePackage(statePath, legacyPath, [apipa], InstallFixture); throw new Exception("Locked state was accepted."); }
            catch (IOException) { }
            Check(installCalls == calls, "Unreadable state reached the installer.");
        }
        Check(File.ReadAllText(statePath) == original, "Unreadable-state preflight changed configuration.");
        Console.WriteLine("UPGRADE_UNSAFE_OR_UNREADABLE_INPUT_FAILS_BEFORE_MUTATION=PASS");

        File.Move(statePath, legacyPath);
        AgentInstallationService.UpgradePackage(statePath, legacyPath, [apipa], InstallFixture);
        Check(lastPlan is { PreserveConfiguration: true, Network.Address: "192.0.2.15" }
            && File.ReadAllText(legacyPath) == original && !File.Exists(statePath), "Legacy upgrade lost its saved identity.");
        File.Delete(legacyPath);
        AgentInstallationService.UpgradePackage(statePath, legacyPath, [wifi], InstallFixture);
        Check(lastPlan is { PreserveConfiguration: true, Network: null } && !File.Exists(statePath),
            "New unconfigured installation silently selected a network.");
        Console.WriteLine("UPGRADE_LEGACY_AND_NEW_UNCONFIGURED_CONTROLS=PASS");

        foreach (var invalid in new[] { apipa, wifi with { Id = "invalid" }, wifi with { Address = "127.0.0.1" }, wifi with { PrefixLength = 32 }, wifi with { Name = "" } })
            Reject(() => AgentInstallationService.ValidateInstallationNetwork(invalid));
        AgentInstallationService.ValidateInstallationNetwork(wifi);
        AgentInstallationService.ValidateInstallationNetwork(null);
        var applied = false;
        AgentInstallationService.ApplyInstallationConfiguration(new(wifi, false), selected => applied = selected == wifi);
        Check(applied, "Explicit onboarding/initial setup no longer applies its validated network selection.");
        Console.WriteLine("SHARED_INSTALL_NETWORK_PREFLIGHT=PASS");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
