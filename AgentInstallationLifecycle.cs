using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace KiloviewPcOnboarding;

internal sealed record AgentLifecycleEvidence(string ProductId, bool? Installed, bool? Configured, bool? Running, bool? Healthy,
    string? Version, string? Channel, string? SetupVersion, bool? PairMatches, bool? Onboarded,
    bool? NetworkAvailable, bool RecoveryRequired, string? Error);

internal static partial class AgentInstallationService
{
    private static string MaintenanceDirectory => Path.Combine(InstallDirectory, "Maintenance");
    private static string OwnerReceipt => Path.Combine(InstallDirectory, "installation-owner.json");
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NDIConfiguratorPcAgent";

    internal static AgentLifecycleEvidence InspectLifecycle() => InspectLifecycle(InstallDirectory, StatePath, LegacyStatePath,
        PackageInstallation.VersionOf, InspectAgentRunning, () => NetworkService.GetChoices());

    private static bool? InspectAgentRunning()
    {
        var unknown = false;
        foreach (var process in Process.GetProcessesByName("NDI Configurator PC Agent"))
            using (process)
                try { if (string.Equals(process.MainModule?.FileName, InstalledAgentPath, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { unknown = true; }
        return unknown ? null : false;
    }

    internal static AgentLifecycleEvidence InspectLifecycle(string directory, string statePath, string legacyPath,
        Func<string, string?> versionOf, Func<bool?> running, Func<IReadOnlyList<NetworkChoice>> networks)
    {
        string? error = null, agent = null, setup = null;
        AgentState? state = null;
        bool? isRunning = null, networkAvailable = null, matches = null, configured = null, onboarded = null;
        var recovery = File.Exists(Path.Combine(directory, ".pc-agent-install-recovery", "manifest.json"))
            || File.Exists(Path.Combine(directory, "removal-state.json"));
        try
        {
            agent = versionOf(Path.Combine(directory, "NDI Configurator PC Agent.exe"));
            setup = versionOf(Path.Combine(directory, "NDI Configurator PC Agent Setup.exe"));
            matches = agent is not null && setup is not null && PackageInstallation.Compare(agent, setup) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { error = ex.Message; }
        try
        {
            // An invalid current state must never fall back to a stale legacy file.
            state = ReadUpgradeState(statePath) ?? ReadUpgradeState(legacyPath);
            configured = state is not null;
            onboarded = state?.Memberships.Count > 0;
        }
        catch (InvalidDataException ex) { error ??= ex.Message; configured = false; onboarded = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
        try
        {
            isRunning = running();
            if (state is not null) networkAvailable = networks().Any(n => n.Id.Equals(state.AdapterId, StringComparison.OrdinalIgnoreCase)
                && NdiSuite.Configuration.AgentConfigurationValidity.IsValidNetwork(n.Id, n.Address, n.PrefixLength));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        { error = ex.Message; }
        if (matches == false && (agent is not null || setup is not null)) error ??= "The installed Setup and Agent pair is missing or mismatched.";
        return new("pc-agent", recovery ? false : matches, configured, isRunning, null,
            agent?.Split('+')[0], agent is null ? null : agent.Split('+')[0].Contains('-') ? "Development" : "Production",
            setup?.Split('+')[0], matches, onboarded, networkAvailable, recovery, error);
    }

    private static void ValidateLifecycleOwner()
    {
        LifecycleOperation.SafePath(InstallDirectory);
        LifecycleOperation.SafePath(StateDirectory);
        LifecycleOperation.SafePath(OwnerReceipt);
        if (!File.Exists(OwnerReceipt)) return;
        using var receipt = JsonDocument.Parse(File.ReadAllText(OwnerReceipt));
        if (!receipt.RootElement.TryGetProperty("ownerSid", out var owner) || owner.GetString() != InteractiveProfile.Sid)
            throw new UnauthorizedAccessException("PC Agent maintenance must run from the desktop account that installed it.");
    }

    private static void PrepareMaintenance(string utility, string agent)
    {
        LifecycleOperation.SafePath(MaintenanceDirectory);
        Directory.CreateDirectory(MaintenanceDirectory);
        var sources = new[] { utility, agent }.SelectMany(exe => Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".*"))
            .Select(source => (source, destination: Path.Combine(MaintenanceDirectory, Path.GetFileName(source))))
            .Where(pair => !PackageInstallation.FilesEqual(pair.source, pair.destination))
            .ToDictionary(pair => pair.destination, pair => pair.source, StringComparer.OrdinalIgnoreCase);
        PackageInstallation.Replace(sources, () => { });
    }

    private static void RegisterInstalledApplication()
    {
        LifecycleOperation.WriteAtomic(OwnerReceipt, new { schemaVersion = 1, ownerSid = InteractiveProfile.Sid });
        LifecycleOperation.WriteAtomic(Path.Combine(InstallDirectory, "toolbox-lifecycle.json"), new {
            schemaVersion = 1, productId = "pc-agent", supportedActions = new[] { "inspect", "install", "update", "repair", "uninstall" },
            entryPoint = "Maintenance/NDI Configurator PC Agent Setup.exe", entryPointKind = "native" });
        using var hive = InteractiveProfile.OpenUserHive();
        using var key = hive.CreateSubKey(UninstallRegistryKey, true);
        key.SetValue("DisplayName", "NDI Configurator PC Agent");
        key.SetValue("DisplayVersion", PackageInstallation.VersionOf(InstalledAgentPath) ?? "unknown");
        key.SetValue("Publisher", "John Lightfoot");
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("DisplayIcon", InstalledAgentPath);
        key.SetValue("UninstallString", $"\"{Path.Combine(MaintenanceDirectory, "NDI Configurator PC Agent Setup.exe")}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    internal static int RunInteractiveUninstall()
    {
        if (!AgentLifecycle.IsAdministrator()) return AgentLifecycle.Elevate(["--uninstall"]);
        ApplicationConfiguration.Initialize();
        if (MessageBox.Show("Remove PC Agent software and its startup/firewall entries? Saved configuration, endpoint identity, NDI Tools and NDI settings will be retained.",
            "Remove NDI Configurator PC Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1602;
        using var lease = SetupOperationLease.Acquire(timeout: TimeSpan.Zero);
        UninstallLifecycle();
        return 0;
    }

    internal static string[] UninstallLifecycle()
    {
        ValidateLifecycleOwner();
        using var packageLock = PackageInstallation.AcquireInstallationLock(TimeSpan.Zero);
        if (File.Exists(Path.Combine(InstallDirectory, ".pc-agent-install-recovery", "manifest.json")))
            throw new IOException("Finish package recovery before removing the Agent.");
        if (!File.Exists(OwnerReceipt) && (File.Exists(InstalledAgentPath) || File.Exists(InstalledUtilityPath)))
            throw new IOException("Repair this legacy installation first so its desktop ownership and retained maintenance files can be verified.");
        var files = Directory.Exists(InstallDirectory)
            ? Directory.EnumerateFiles(InstallDirectory).Where(path => Path.GetFileName(path).StartsWith("NDI Configurator PC Agent.", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith("NDI Configurator PC Agent Setup.", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
        foreach (var file in files) LifecycleOperation.SafePath(file);
        var journal = Path.Combine(InstallDirectory, "removal-state.json");
        Directory.CreateDirectory(InstallDirectory);
        LifecycleOperation.WriteAtomic(journal, new { schemaVersion = 1, phase = "removing", updatedUtc = DateTimeOffset.UtcNow });
        var effects = RemoveOwnedFiles(files, () =>
        {
            StopInstalledAgent();
            if (IsAgentRunning()) throw new IOException("The installed Agent could not be stopped; no files were removed.");
        }, () =>
        {
            using var hive = InteractiveProfile.OpenUserHive();
            using var run = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue(RunValueName, false);
            foreach (var name in new[] { DiscoveryRuleName, ApiRuleName }) RemoveOwnedFirewallRule(name);
        }, () =>
        {
            using var hive = InteractiveProfile.OpenUserHive();
            hive.DeleteSubKeyTree(UninstallRegistryKey, false);
        });
        File.Delete(journal);
        return effects;
    }

    internal static string[] RemoveOwnedFiles(IEnumerable<string> files, Action stop, Action unregisterStartup, Action unregisterApp)
    {
        var effects = new List<string>();
        var errors = new List<string>();
        stop(); effects.Add("Stopped the installed PC Agent.");
        try { unregisterStartup(); effects.Add("Removed owned startup and firewall entries."); }
        catch (Exception ex) { errors.Add(ex.Message); }
        foreach (var file in files)
            try { LifecycleOperation.SafePath(file); File.Delete(file); if (File.Exists(file)) throw new IOException("File remains: " + file); }
            catch (Exception ex) { errors.Add(ex.Message); }
        if (errors.Count == 0)
            try { unregisterApp(); effects.Add("Removed PC Agent application files and Installed Apps registration."); }
            catch (Exception ex) { errors.Add(ex.Message); }
        if (errors.Count > 0) throw new LifecyclePartialException("PC Agent removal is incomplete. Retry from its retained maintenance entry point. " + string.Join("; ", errors), effects.ToArray());
        return effects.ToArray();
    }

    private static void RemoveOwnedFirewallRule(string name)
    {
        object? policy = null, rules = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new IOException("Windows firewall API is unavailable."));
            rules = ((dynamic)policy!).Rules;
            RemoveOwnedFirewallRule(rules, name, InstalledAgentPath);
        }
        finally { ReleaseComObject(rules); ReleaseComObject(policy); }
    }

    internal static void RemoveOwnedFirewallRule(object rules, string name, string installedAgentPath)
    {
        object? rule = null;
        try
        {
            try { rule = ((dynamic)rules).Item(name); }
            catch (Exception ex) when (IsMissingFirewallRule(ex)) { return; }
            if (!string.Equals((string)((dynamic)rule).ApplicationName, installedAgentPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A same-named firewall rule belongs to another executable: " + name);
            try { ((dynamic)rules).Remove(name); }
            catch (Exception ex) when (IsMissingFirewallRule(ex)) { } // Verify absence below, including concurrent removal.
            ReleaseComObject(rule);
            rule = null;
            try { rule = ((dynamic)rules).Item(name); }
            catch (Exception ex) when (IsMissingFirewallRule(ex)) { return; }
            throw new IOException("The firewall rule remains: " + name);
        }
        finally { ReleaseComObject(rule); }
    }

    // The Windows COM binder maps ERROR_FILE_NOT_FOUND to FileNotFoundException
    // on .NET, while other interop paths expose COMException. No other HRESULT
    // or operation (policy activation, permissions, owner read) means absence.
    private static bool IsMissingFirewallRule(Exception error) =>
        error is System.Runtime.InteropServices.COMException or FileNotFoundException
        && (uint)error.HResult == 0x80070002;
}
