using System.Text.Json;
using KiloviewPcOnboarding;

internal static class LifecycleTargetValidation
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "lifecycle-target", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        LifecycleValidation.SecureDirectory(directory);
        var request = new LifecycleRequest(1, Path.GetFileName(directory), "pc-agent", "update",
            Path.Combine(directory, "result.json"), RequestedVersion: "0.7.5-dev.1");
        var requestPath = Path.Combine(directory, "request.json");
        LifecycleOperation.WriteAtomic(requestPath, request);
        var operation = new LifecycleOperation(requestPath);
        AgentLifecycleEvidence Retained() => new("pc-agent", true, true, true, null,
            "0.7.5", "Production", "0.7.5", true, false, true, false, null);
        var exit = AgentLifecycle.Execute(operation,
            op => AgentLifecycle.InstallPackage(op, _ => new(true, true, "Existing newer pair retained."), Retained), Retained);
        var result = JsonSerializer.Deserialize<LifecycleResult>(File.ReadAllText(request.ResultPath), LifecycleOperation.Json)!;
        if (exit == 0 || result.Outcome == "succeeded")
            throw new Exception("Explicit lifecycle target was reported successful with the retained Production pair. Evidence: " + request.ResultPath);
        foreach (var invalid in new[] { Retained() with { Version = "0.7.5-dev.1", SetupVersion = "0.7.5" },
                     Retained() with { Version = "0.7.5-dev.1", SetupVersion = null } })
        {
            var attempt = RunOperation(root, "0.7.5-dev.1", _ => new(true, true, "Fixture install returned success."), () => invalid);
            Check(attempt.Exit != 0 && attempt.Result.Outcome != "succeeded", "Incomplete target pair reported success.");
        }
        Console.WriteLine("LIFECYCLE_TARGET_POSTCONDITION=PASS");

        foreach (var pair in new[] { (Installed: "0.7.5", Target: "0.7.5-dev.1"),
                     (Installed: "0.7.5-dev.1", Target: "0.7.5"), (Installed: "0.7.6", Target: "0.7.5-dev.1") })
        {
            var fixture = new TargetFixture(root, pair.Installed, pair.Target);
            var attempt = fixture.Run(pair.Target);
            Check(attempt.Exit == 0 && attempt.Result.Outcome == "succeeded" && fixture.Replacements == 1,
                "Explicit matching target did not replace the existing pair.");
            var after = fixture.Inspect();
            Check(after.Version == pair.Target && after.SetupVersion == pair.Target && after.PairMatches == true,
                "Explicit transition did not install both actual target versions.");
            fixture.AssertPreserved();
            Check(fixture.LastPlan?.RequestedVersion == pair.Target, "Lifecycle target was lost before installation policy.");
        }
        var unconfigured = new TargetFixture(root, "0.7.5", "0.7.5-dev.1");
        File.Delete(unconfigured.StatePath);
        Check(unconfigured.Run("0.7.5-dev.1").Exit == 0 && unconfigured.LastPlan?.Network is null
            && !File.Exists(unconfigured.StatePath), "Explicit unconfigured transition selected or configured a network.");
        Console.WriteLine("LIFECYCLE_EXPLICIT_TARGET_REPLACEMENT_PRESERVES_STATE=PASS");

        var implicitUpgrade = new TargetFixture(root, "0.7.5", "0.7.5-dev.1");
        var retained = implicitUpgrade.Run(null);
        Check(retained.Exit == 0 && implicitUpgrade.Replacements == 0 && implicitUpgrade.Inspect().Version == "0.7.5"
            && implicitUpgrade.LastPlan?.RequestedVersion is null, "No-target lifecycle request replaced a newer pair.");
        implicitUpgrade.AssertPreserved();
        // This is also the unchanged default used by --server-command, normal Setup and onboarding.
        Check(PackageInstallation.Retain("0.7.5", "0.7.5", "0.7.5-dev.1")
            && new AgentInstallationService.InstallationPlan(null, false).RequestedVersion is null,
            "Implicit bundled/onboarding defaults gained an explicit target.");
        Console.WriteLine("LIFECYCLE_IMPLICIT_NEWER_RETENTION_UNCHANGED=PASS");

        foreach (var wrong in new[] { "0.7.5", "0.7.4-dev.1", "v0.7.5-dev.1", "", " 0.7.5-dev.1", "0.7.5-dev.01" })
        {
            var fixture = new TargetFixture(root, "0.7.5", "0.7.5-dev.1");
            fixture.AssertRejected(fixture.Run(wrong));
        }
        foreach (var missingOrMixed in new string?[] { null, "0.7.5", "0.7.4-dev.1" })
        {
            var fixture = new TargetFixture(root, "0.7.5", "0.7.5-dev.1");
            if (missingOrMixed is null) File.Delete(fixture.SourceAgent);
            else File.WriteAllText(fixture.SourceAgent, missingOrMixed);
            fixture.AssertRejected(fixture.Run("0.7.5-dev.1"));
        }
        var mixedInstalled = new TargetFixture(root, "0.7.5", "0.7.5-dev.1");
        File.WriteAllText(mixedInstalled.InstalledSetup, "0.7.4");
        mixedInstalled.AssertRejected(mixedInstalled.Run("0.7.5-dev.1"));
        var invalidState = new TargetFixture(root, "0.7.5", "0.7.5-dev.1");
        File.WriteAllText(invalidState.StatePath, "invalid");
        var invalidAttempt = invalidState.Run("0.7.5-dev.1");
        Check(invalidAttempt.Exit != 0 && invalidState.LastPlan is null && invalidState.Replacements == 0
            && File.ReadAllText(invalidState.StatePath) == "invalid", "Explicit target bypassed saved-state preflight.");
        Console.WriteLine("LIFECYCLE_TARGET_PACKAGE_AND_INTENT_REJECTION=PASS");
        Console.WriteLine("LIFECYCLE_TARGET_EVIDENCE=" + root);
    }

    private static (int Exit, LifecycleResult Result) RunOperation(string root, string? requested,
        Func<string?, AgentInstallationResult> upgrade, Func<AgentLifecycleEvidence> inspect)
    {
        var directory = Path.Combine(root, "lifecycle-target", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory); LifecycleValidation.SecureDirectory(directory);
        var request = new LifecycleRequest(1, Path.GetFileName(directory), "pc-agent", "update",
            Path.Combine(directory, "result.json"), RequestedVersion: requested);
        var requestPath = Path.Combine(directory, "request.json");
        LifecycleOperation.WriteAtomic(requestPath, request);
        var operation = new LifecycleOperation(requestPath);
        var exit = AgentLifecycle.Execute(operation, op => AgentLifecycle.InstallPackage(op, upgrade, inspect), inspect);
        return (exit, JsonSerializer.Deserialize<LifecycleResult>(File.ReadAllText(request.ResultPath), LifecycleOperation.Json)!);
    }

    private sealed class TargetFixture
    {
        private readonly string root, installed, sourceSetup, legacyState, originalState, initialAgent;
        private readonly NetworkChoice network;
        internal string StatePath { get; }
        internal string SourceAgent { get; }
        internal string InstalledSetup { get; }
        private string InstalledAgent => Path.Combine(installed, "NDI Configurator PC Agent.exe");
        internal int Replacements { get; private set; }
        private int configurationWrites;
        internal AgentInstallationService.InstallationPlan? LastPlan { get; private set; }

        internal TargetFixture(string root, string previous, string target)
        {
            this.root = root;
            var directory = Path.Combine(root, "target-package-" + Guid.NewGuid().ToString("N"));
            installed = Path.Combine(directory, "installed");
            Directory.CreateDirectory(installed);
            sourceSetup = Path.Combine(directory, "source-setup.exe");
            SourceAgent = Path.Combine(directory, "source-agent.exe");
            InstalledSetup = Path.Combine(installed, "NDI Configurator PC Agent Setup.exe");
            StatePath = Path.Combine(directory, "agent-state.json");
            legacyState = Path.Combine(directory, "legacy-state.json");
            initialAgent = previous;
            File.WriteAllText(InstalledAgent, previous); File.WriteAllText(InstalledSetup, previous);
            File.WriteAllText(sourceSetup, target + "+fixture"); File.WriteAllText(SourceAgent, target + "+fixture");
            network = new(Guid.NewGuid().ToString("D"), "Ethernet", "Fixture", "192.0.2.15", 24);
            originalState = JsonSerializer.Serialize(new { schemaVersion = 1, endpointId = Guid.NewGuid().ToString("D"),
                adapterId = network.Id, adapterName = network.Name, address = network.Address, prefixLength = 24,
                memberships = new[] { new { serverAddress = "192.0.2.10", baseUri = "http://192.0.2.10:8091/", jobName = "Retained job" } } });
            File.WriteAllText(StatePath, originalState);
        }

        internal AgentLifecycleEvidence Inspect() => AgentInstallationService.InspectLifecycle(installed, StatePath, legacyState,
            Version, () => false, () => [network]);

        internal (int Exit, LifecycleResult Result) Run(string? requested) => RunOperation(root, requested,
            target => AgentInstallationService.UpgradePackage(StatePath, legacyState,
                [network with { Address = "192.0.2.16" }], Install, target), Inspect);

        private AgentInstallationResult Install(AgentInstallationService.InstallationPlan plan)
        {
            LastPlan = plan;
            // Real package policy and transaction, with temporary text files supplying
            // metadata instead of native executables. No Windows install callbacks run.
            var package = Version(sourceSetup);
            PackageInstallation.ValidatePackageTarget(package, Version(SourceAgent), plan.RequestedVersion);
            if (!PackageInstallation.Retain(Version(InstalledAgent), Version(InstalledSetup), package!, plan.RequestedVersion))
                PackageInstallation.Replace(new Dictionary<string, string> { [InstalledAgent] = SourceAgent, [InstalledSetup] = sourceSetup }, () => Replacements++);
            AgentInstallationService.ApplyInstallationConfiguration(plan, _ => configurationWrites++);
            return AgentInstallationService.CompletedInstallationResult(plan.Network, false);
        }

        internal void AssertPreserved() => Check(File.ReadAllText(StatePath) == originalState && configurationWrites == 0,
            "Target transition altered saved identity, adapter, memberships or NDI configuration.");

        internal void AssertRejected((int Exit, LifecycleResult Result) result)
        {
            Check(result.Exit != 0 && result.Result.Outcome != "succeeded" && Replacements == 0
                && File.ReadAllText(InstalledAgent) == initialAgent, "Invalid target/package reached replacement or reported success.");
            AssertPreserved();
        }

        private static string? Version(string file) => File.Exists(file) ? File.ReadAllText(file) : null;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
