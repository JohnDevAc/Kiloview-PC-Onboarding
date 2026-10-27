using System.Runtime.InteropServices;
using System.Text.Json;
using KiloviewPcOnboarding;

internal static class FirewallRemovalValidation
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "fixture-agent.exe");
        var name = "NDI Agent absent-rule validation " + Guid.NewGuid().ToString("N");
        object? policy = null, nativeRules = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            nativeRules = ((dynamic)policy!).Rules;
            var readOnly = new ReadOnlyFirewallRules(nativeRules);
            Exception? missing = null;
            try { _ = readOnly.Item(name); }
            catch (Exception ex) { missing = ex; }
            if (missing is null || (uint)missing.HResult != 0x80070002)
                throw new Exception("Native missing-rule lookup did not return ERROR_FILE_NOT_FOUND.", missing);
            File.WriteAllText(Path.Combine(root, "firewall-removal-boundary.json"), JsonSerializer.Serialize(new
            {
                capturedUtc = DateTimeOffset.UtcNow, ruleName = name, exceptionType = missing.GetType().FullName,
                hresult = $"0x{missing.HResult:X8}", exception = missing.ToString(), mutationsAllowed = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"NATIVE_MISSING_FIREWALL_RULE={missing.GetType().FullName}:0x{missing.HResult:X8}");
            // The wrapper exposes real Windows lookup but never allows a native Remove.
            AgentInstallationService.RemoveOwnedFirewallRule(readOnly, name, executable);
        }
        finally
        {
            if (nativeRules is not null && Marshal.IsComObject(nativeRules)) Marshal.ReleaseComObject(nativeRules);
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
        Console.WriteLine("LIFECYCLE_NATIVE_ABSENT_FIREWALL_RULE=PASS");

        foreach (var absent in new Exception[] { new FileNotFoundException("Missing optional rule"), new COMException("Missing optional rule", unchecked((int)0x80070002)) })
        {
            var rules = new FixtureFirewallRules { Missing = absent };
            AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable);
            Check(rules.RemoveCalls == 0, "An absent rule reached removal.");
            rules.Owner = executable;
            AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable);
            AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable);
            Check(rules.RemoveCalls == 1 && rules.Owner is null, "Owned-rule removal was not idempotent.");
            rules.Owner = executable; rules.RemoveFailure = absent;
            ExpectFailure(() => AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable), typeof(IOException));
            rules.DisappearBeforeFailure = true;
            AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable);
            Check(rules.Owner is null, "Concurrent removal did not verify absence.");
        }
        var foreign = new FixtureFirewallRules { Owner = executable + ".other" };
        ExpectFailure(() => AgentInstallationService.RemoveOwnedFirewallRule(foreign, name, executable), typeof(IOException));
        Check(foreign.RemoveCalls == 0, "A foreign-owned rule was removed.");
        foreach (var error in new Exception[] { new UnauthorizedAccessException("Denied"), new COMException("Denied", unchecked((int)0x80070005)),
                     new COMException("Unavailable", unchecked((int)0x800706BA)), new COMException("Wrong missing-resource status", unchecked((int)0x80070003)) })
        {
            var rules = new FixtureFirewallRules { LookupFailure = error };
            ExpectFailure(() => AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable), error.GetType());
            Check(rules.RemoveCalls == 0, "Lookup failure reached removal.");
            rules.LookupFailure = null; rules.Owner = executable; rules.RemoveFailure = error;
            ExpectFailure(() => AgentInstallationService.RemoveOwnedFirewallRule(rules, name, executable), error.GetType());
            Check(rules.Owner == executable, "Removal failure was hidden.");
        }
        var retained = new FixtureFirewallRules { Owner = executable, KeepRule = true };
        ExpectFailure(() => AgentInstallationService.RemoveOwnedFirewallRule(retained, name, executable), typeof(IOException));

        var registrationRemoved = false;
        var files = new[] { Path.Combine(root, "optional-removal-fixture.exe") };
        File.WriteAllText(files[0], "fixture");
        var optional = new FixtureFirewallRules();
        AgentInstallationService.RemoveOwnedFiles(files, () => { },
            () => AgentInstallationService.RemoveOwnedFirewallRule(optional, name, executable), () => registrationRemoved = true);
        Check(registrationRemoved && !File.Exists(files[0]), "Absent optional firewall rule prevented complete package removal.");
        registrationRemoved = false;
        try
        {
            AgentInstallationService.RemoveOwnedFiles([], () => { },
                () => AgentInstallationService.RemoveOwnedFirewallRule(foreign, name, executable), () => registrationRemoved = true);
            throw new Exception("Foreign ownership did not preserve partial removal.");
        }
        catch (LifecyclePartialException) { Check(!registrationRemoved, "Partial removal lost its retry registration."); }
        Console.WriteLine("LIFECYCLE_OPTIONAL_FIREWALL_REMOVAL_AND_ERRORS=PASS");
        Console.WriteLine("FIREWALL_REMOVAL_EVIDENCE=" + Path.Combine(root, "firewall-removal-boundary.json"));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectFailure(Action action, Type type)
    {
        try { action(); }
        catch (Exception ex) when (ex.GetType() == type) { return; }
        throw new Exception("A firewall ownership/permission/postcondition error was swallowed.");
    }
}

public sealed class ReadOnlyFirewallRules(object rules)
{
    public object Item(string name) => ((dynamic)rules).Item(name);
    public void Remove(string name) => throw new InvalidOperationException("Native firewall mutation is forbidden in this fixture.");
}

public sealed class FixtureFirewallRule(string applicationName)
{
    public string ApplicationName => applicationName;
}

public sealed class FixtureFirewallRules
{
    public string? Owner { get; set; }
    public Exception Missing { get; set; } = new FileNotFoundException("Missing optional rule");
    public Exception? LookupFailure { get; set; }
    public Exception? RemoveFailure { get; set; }
    public int RemoveCalls { get; private set; }
    public bool KeepRule { get; set; }
    public bool DisappearBeforeFailure { get; set; }
    public object Item(string name)
    {
        if (LookupFailure is not null) throw LookupFailure;
        return Owner is null ? throw Missing : new FixtureFirewallRule(Owner);
    }
    public void Remove(string name)
    {
        RemoveCalls++;
        if (RemoveFailure is not null)
        {
            if (DisappearBeforeFailure) Owner = null;
            throw RemoveFailure;
        }
        if (!KeepRule) Owner = null;
    }
}
