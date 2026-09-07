using KiloviewPcOnboarding;

internal static class QaRegression
{
    internal static async Task RunAsync(string root)
    {
        var endpoint = Guid.NewGuid().ToString(); var adapter = Guid.NewGuid().ToString();
        Check(NdiSuite.Configuration.AgentConfigurationValidity.IsValid(1, endpoint, adapter, "192.0.2.20", 24), "Valid saved identity was rejected.");
        foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "169.254.1.2", "224.0.0.1", "192.0.2.0", "192.0.2.255" })
            Check(!NdiSuite.Configuration.AgentConfigurationValidity.IsValid(1, endpoint, adapter, address, 24), "Unusable saved address was accepted.");
        Console.WriteLine("SETUP_PERSISTED_IDENTITY_VALIDATION=PASS");
        // Exercise Windows' real VARIANT marshalling without adding a firewall rule.
        var interfaceName = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .First(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback).Name;
        foreach (var (protocol, port) in new[] { (17, 8093), (6, 8094) })
        {
            var native = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            try
            {
                var nativePolicy = new AgentFirewallPolicy(protocol, port, interfaceName, Path.Combine(root, "agent.exe"));
                nativePolicy.Apply(native);
                Check(nativePolicy.Matches(native), "Windows rejected the native interface-scoped firewall policy.");
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(native); }
        }
        Console.WriteLine("NATIVE_FIREWALL_VARIANT_MARSHALLING=PASS");
        var nativeFailure = new ArgumentException("Native firewall fixture failure");
        try
        {
            new AgentInstallationResult(false, false, "Installation failed", nativeFailure).EnsureInstalled();
            throw new Exception("Failed installation was accepted.");
        }
        catch (InvalidOperationException ex)
        {
            Check(ReferenceEquals(ex.InnerException, nativeFailure), "The native installation exception was discarded.");
            var trace = new NdiSuite.Onboarding.OnboardingTrace();
            var report = trace.Failure(endpoint, Guid.NewGuid().ToString(), "fixture", ex);
            Check(report.StackTrace.Contains("Native firewall fixture failure"), "Diagnostic evidence lost the underlying installation fault.");
        }
        Console.WriteLine("INSTALLATION_DIAGNOSTIC_INNER_EXCEPTION=PASS");
        var policy = new AgentFirewallPolicy(17, 8093, "Production", Path.Combine(root, "agent.exe"));
        var rule = new FirewallFixture();
        policy.Apply(rule);
        Check(policy.Matches(rule), "The verifier rejected the policy written by Setup.");
        rule.RemoteAddresses = " localsubnet ";
        rule.Interfaces = new object[] { "PRODUCTION" };
        rule.ApplicationName = rule.ApplicationName.ToUpperInvariant();
        Check(policy.Matches(rule), "Normal COM casing/array normalization was rejected.");
        foreach (Action<FirewallFixture> corrupt in new Action<FirewallFixture>[] {
            r => r.Interfaces = new[] { "Internet" }, r => r.ApplicationName = Path.Combine(root, "other.exe"),
            r => r.RemoteAddresses = "*", r => r.LocalAddresses = "192.0.2.15",
            r => r.Profiles = 3, r => r.EdgeTraversal = true, r => r.LocalPorts = "8094",
            r => r.Direction = 2, r => r.Action = 0, r => r.Protocol = 6, r => r.Enabled = false })
        {
            policy.Apply(rule); corrupt(rule);
            Check(!policy.Matches(rule), "An incorrect firewall field passed verification.");
        }
        Console.WriteLine("FIREWALL_WRITER_AND_VERIFIER=PASS");

        var leasePath = Path.Combine(root, "operation.lock");
        using (SetupOperationLease.Acquire(leasePath))
        {
            var blocked = await Task.Run(() => {
                try { using var other = SetupOperationLease.Acquire(leasePath, TimeSpan.Zero); return false; }
                catch (IOException) { return true; }
            });
            Check(blocked, "A concurrent Setup operation acquired the process lease.");
        }
        using (SetupOperationLease.Acquire(leasePath, TimeSpan.Zero)) { }
        Console.WriteLine("SETUP_PROCESS_LIFETIME_LEASE=PASS");

        var directory = Path.Combine(root, "locked-recovery");
        Directory.CreateDirectory(directory);
        var agent = Path.Combine(directory, "agent.exe");
        var setup = Path.Combine(directory, "setup.exe");
        var source = Path.Combine(root, "new-binary");
        File.WriteAllText(agent, "old"); File.WriteAllText(setup, "old"); File.WriteAllText(source, "new");
        using (File.Open(setup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { PackageInstallation.Replace(new Dictionary<string, string> { [agent] = source, [setup] = source }, () => { }); }
            catch (IOException) { }
            Check(File.ReadAllText(agent) == "old" && File.ReadAllText(setup) == "old", "Locked replacement did not restore the pair.");
            Check(!Directory.Exists(Path.Combine(directory, ".pc-agent-install-recovery")), "An unchanged locked Setup left a stuck recovery journal.");
            PackageInstallation.Recover(directory);
        }
        // An unpublished journal means no live replacement began.
        var recovery = Path.Combine(directory, ".pc-agent-install-recovery");
        Directory.CreateDirectory(recovery);
        File.WriteAllText(Path.Combine(recovery, "manifest.tmp"), "{partial");
        PackageInstallation.Recover(directory);
        Check(File.ReadAllText(agent) == "old", "An interrupted journal publication changed a live binary.");
        Directory.CreateDirectory(recovery);
        File.WriteAllText(Path.Combine(recovery, "setup.exe"), "old");
        File.WriteAllText(Path.Combine(recovery, "manifest.json"), "{\"setup.exe\":true}");
        File.WriteAllText(setup, "partially-updated");
        try { PackageInstallation.Recover(directory, setup); throw new Exception("Running Setup replacement was not deferred."); }
        catch (PackageInstallation.RunningSetupRecoveryException) { }
        Check(File.Exists(Path.Combine(recovery, "manifest.json")) && File.ReadAllText(setup) == "partially-updated",
            "Deferred recovery lost the journal or attempted to replace its running process.");
        PackageInstallation.Recover(directory, Path.Combine(root, "external-helper.exe"));
        Check(File.ReadAllText(setup) == "old" && !Directory.Exists(recovery), "External recovery did not restore and clear the saved package.");
        Console.WriteLine("LOCKED_SETUP_AND_ATOMIC_JOURNAL_RECOVERY=PASS");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

// Public members let the production dynamic COM adapter exercise its real writer/verifier.
public sealed class FirewallFixture
{
    public int Protocol { get; set; }
    public string LocalPorts { get; set; } = "";
    public int Direction { get; set; }
    public int Action { get; set; }
    public int Profiles { get; set; }
    public string LocalAddresses { get; set; } = "";
    public string RemoteAddresses { get; set; } = "";
    public Array Interfaces { get; set; } = Array.Empty<string>();
    public string ApplicationName { get; set; } = "";
    public bool EdgeTraversal { get; set; }
    public bool Enabled { get; set; }
}
