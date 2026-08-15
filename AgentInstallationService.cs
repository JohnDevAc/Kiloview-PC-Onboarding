using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KiloviewPcOnboarding;

internal sealed record AgentInstallationResult(
    bool Installed,
    bool Started,
    string Message);

internal static class AgentInstallationService
{
    internal const int DiscoveryPort = 8093;
    internal const int ApiPort = 8094;
    internal const string DiscoveryRuleName = "NDI Configurator PC Agent - Discovery";
    internal const string ApiRuleName = "NDI Configurator PC Agent - Monitoring";
    private const string LegacyDiscoveryRuleName = "Kiloview PC Agent - Discovery";
    private const string LegacyApiRuleName = "Kiloview PC Agent - Monitoring";
    private const string LegacyPingRuleName = "Kiloview PC Onboarding - ICMPv4 Echo";
    private const string RunValueName = "NDI Configurator PC Agent";
    private const string LegacyRunValueName = "Kiloview PC Agent";
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int AllProfiles = int.MaxValue;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string InstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "NDI Configurator",
        "PC Agent");
    private static readonly string LegacyInstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Kiloview",
        "PC Agent");
    private static readonly string InstalledAgentPath = Path.Combine(
        InstallDirectory,
        "NDI Configurator PC Agent.exe");
    private static readonly string InstalledUtilityPath = Path.Combine(
        InstallDirectory,
        "NDI Configurator PC Agent Setup.exe");
    private static readonly string LegacyInstalledAgentPath = Path.Combine(
        LegacyInstallDirectory,
        "Kiloview PC Agent.exe");
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NDI Configurator",
        "PC Agent");
    private static readonly string StatePath = Path.Combine(StateDirectory, "agent-state.json");
    private static readonly string LegacyStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kiloview",
        "PC Agent",
        "agent-state.json");

    public static AgentInstallationResult InstallOrUpdate(NetworkChoice network)
    {
        var sourceAgent = ResolveAgentPayload();
        if (sourceAgent is null)
        {
            return new(
                false,
                false,
                "The NDI Configurator PC Agent payload is missing. Run Setup from its complete release package.");
        }

        try
        {
            Directory.CreateDirectory(InstallDirectory);
            Directory.CreateDirectory(StateDirectory);
            MigrateLegacyState();
            var agentChanged = !PublishedFilesMatch(
                Path.GetDirectoryName(sourceAgent)!,
                Path.GetFileNameWithoutExtension(sourceAgent));
            if (agentChanged)
            {
                StopInstalledAgent();
                CopyPublishedFiles(
                    Path.GetDirectoryName(sourceAgent)!,
                    Path.GetFileNameWithoutExtension(sourceAgent));
            }

            var runningUtility = Environment.ProcessPath
                ?? throw new InvalidOperationException("The onboarding executable path is unavailable.");
            if (!Path.GetFullPath(runningUtility).Equals(
                Path.GetFullPath(InstalledUtilityPath),
                StringComparison.OrdinalIgnoreCase))
                CopyPublishedFiles(
                    Path.GetDirectoryName(runningUtility)!,
                    Path.GetFileNameWithoutExtension(runningUtility));

            UpdateConfiguration(network, null);
            ConfigureStartup();
            ConfigureLanRules(network);
            RemoveFirewallRule(LegacyDiscoveryRuleName);
            RemoveFirewallRule(LegacyApiRuleName);
            RemoveFirewallRule(LegacyPingRuleName);
            StopLegacyAgent();
            var started = StartAgentIfNeeded();
            return new(
                true,
                started,
                started
                    ? $"NDI Configurator PC Agent installed · discovery UDP {DiscoveryPort} · monitoring TCP {ApiPort}"
                    : "NDI Configurator PC Agent installed and will start at the next user logon.");
        }
        catch (Exception ex)
        {
            return new(false, false, $"NDI Configurator PC Agent installation failed: {ex.Message}");
        }
    }

    public static NetworkChoice? PreferredNetwork()
    {
        var state = ReadState();
        return state is null
            ? null
            : new NetworkChoice(
                state.AdapterId,
                state.AdapterName,
                state.AdapterName,
                state.Address,
                state.PrefixLength);
    }

    public static bool IsConfigured() => ReadState() is not null
        && (File.Exists(InstalledAgentPath) || File.Exists(LegacyInstalledAgentPath));

    public static void RecordMembership(
        NetworkChoice network,
        JobConfiguratorInstance server)
    {
        UpdateConfiguration(network, new AgentMembershipState(
            server.Address,
            server.BaseUri.ToString(),
            server.JobName,
            DateTimeOffset.UtcNow));
    }

    private static void UpdateConfiguration(
        NetworkChoice network,
        AgentMembershipState? membership)
    {
        using var mutex = new Mutex(false, "Local\\KiloviewPcAgentState");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            throw new IOException("The NDI Configurator PC Agent configuration is currently in use.");
        try
        {
            var current = ReadState();
            var memberships = current?.Memberships?.ToList() ?? [];
            if (membership is not null)
            {
                memberships.RemoveAll(item => string.Equals(
                    item.ServerAddress,
                    membership.ServerAddress,
                    StringComparison.OrdinalIgnoreCase));
                memberships.Add(membership);
            }
            var multicast = current?.Multicast;
            if (multicast is not null
                && !memberships.Any(item => string.Equals(
                    item.JobName,
                    multicast.JobName,
                    StringComparison.Ordinal)))
                multicast = null;
            var now = DateTimeOffset.UtcNow;
            var state = new AgentState(
                1,
                ConsentStore.EndpointId(),
                network.Id,
                network.Name,
                network.Address,
                network.PrefixLength,
                current?.InstalledUtc ?? now,
                now,
                memberships.OrderBy(item => item.JobName, StringComparer.OrdinalIgnoreCase).ToArray(),
                multicast);
            var temporaryPath = StatePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, Json));
            File.Move(temporaryPath, StatePath, true);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static AgentState? ReadState()
    {
        try
        {
            var path = File.Exists(StatePath)
                ? StatePath
                : LegacyStatePath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ResolveAgentPayload() => ResolveAgentPayload(AppContext.BaseDirectory);

    internal static string? ResolveAgentPayload(string baseDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Agent", "NDI Configurator PC Agent.exe"),
            Path.Combine(baseDirectory, "NDI Configurator PC Agent.exe"),
            Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Agent",
                "bin",
                "Release",
                "net8.0-windows",
                "win-x64",
                "NDI Configurator PC Agent.exe")
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static void ConfigureStartup()
    {
        using var run = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");
        run.SetValue(RunValueName, $"\"{InstalledAgentPath}\"", RegistryValueKind.String);
        run.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
    }

    private static void MigrateLegacyState()
    {
        using var mutex = new Mutex(false, "Local\\KiloviewPcAgentState");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            throw new IOException("The NDI Configurator PC Agent configuration is currently in use.");
        try
        {
            if (!File.Exists(StatePath) && File.Exists(LegacyStatePath))
                File.Copy(LegacyStatePath, StatePath, overwrite: false);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void ConfigureLanRules(NetworkChoice network)
    {
        var remoteSubnet = NetworkCidr(network);
        AddOrReplaceFirewallRule(
            DiscoveryRuleName,
            "Allows NDI Job Configurator discovery requests from the selected production subnet.",
            17,
            DiscoveryPort,
            network.Address,
            remoteSubnet);
        AddOrReplaceFirewallRule(
            ApiRuleName,
            "Allows NDI Job Configurator to read NDI Configurator PC Agent monitoring status on the selected production subnet.",
            6,
            ApiPort,
            network.Address,
            remoteSubnet);
    }

    private static void AddOrReplaceFirewallRule(
        string name,
        string description,
        int protocol,
        int localPort,
        string localAddress,
        string remoteAddress)
    {
        object? policy = null;
        object? rules = null;
        object? rule = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new InvalidOperationException("Windows Firewall is not available.");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
                ?? throw new InvalidOperationException("Windows Firewall rule management is not available.");
            policy = Activator.CreateInstance(policyType);
            rules = ((dynamic)policy!).Rules;
            rule = Activator.CreateInstance(ruleType);
            dynamic configured = rule!;
            configured.Name = name;
            configured.Description = description;
            configured.Protocol = protocol;
            configured.LocalPorts = localPort.ToString();
            configured.Direction = InboundDirection;
            configured.Action = AllowAction;
            configured.Profiles = AllProfiles;
            configured.LocalAddresses = localAddress;
            configured.RemoteAddresses = remoteAddress;
            configured.EdgeTraversal = false;
            configured.Enabled = true;

            object? existing = null;
            var existingRuleFound = false;
            try
            {
                existing = ((dynamic)rules).Item(name);
                existingRuleFound = true;
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException)
            {
                // The branded rule has not been created on this PC yet.
            }
            finally
            {
                ReleaseComObject(existing);
            }
            if (existingRuleFound)
                ((dynamic)rules).Remove(name);

            ((dynamic)rules).Add(configured);
            ReleaseComObject(rule);
            rule = null;
            if (!FirewallRuleMatches(
                rules,
                name,
                protocol,
                localPort,
                localAddress,
                remoteAddress))
                throw new InvalidOperationException($"Windows did not retain the {name} firewall rule settings.");
        }
        finally
        {
            ReleaseComObject(rule);
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    private static void RemoveFirewallRule(string name)
    {
        object? policy = null;
        object? rules = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
                return;
            policy = Activator.CreateInstance(policyType);
            rules = ((dynamic)policy!).Rules;
            try { ((dynamic)rules).Remove(name); }
            catch (Exception ex) when (ex is COMException or FileNotFoundException) { }
        }
        finally
        {
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    private static bool StartAgentIfNeeded()
    {
        if (IsAgentRunning())
            return true;
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        if (!File.Exists(explorer))
            return false;
        using (Process.Start(new ProcessStartInfo(explorer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstallDirectory,
            ArgumentList = { InstalledAgentPath }
        })) { }
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(100);
            if (IsAgentRunning())
                return true;
        }
        return false;
    }

    private static bool IsAgentRunning() => Process.GetProcessesByName("NDI Configurator PC Agent")
        .Any(process =>
        {
            using (process)
            {
                try
                {
                    return string.Equals(
                        process.MainModule?.FileName,
                        InstalledAgentPath,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        });

    private static void StopInstalledAgent()
    {
        StopAgentProcesses("NDI Configurator PC Agent", InstalledAgentPath);
    }

    private static void StopLegacyAgent()
    {
        StopAgentProcesses("Kiloview PC Agent", LegacyInstalledAgentPath);
    }

    private static void StopAgentProcesses(string processName, string expectedPath)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(
                        process.MainModule?.FileName,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch
                {
                    // Copy below reports a clear failure if the old agent remains locked.
                }
            }
        }
    }

    private static bool PublishedFilesMatch(string sourceDirectory, string baseName)
    {
        var sources = Directory.EnumerateFiles(sourceDirectory, $"{baseName}.*").ToArray();
        return sources.Length > 0 && sources.All(source =>
        {
            var destination = Path.Combine(InstallDirectory, Path.GetFileName(source));
            if (!File.Exists(destination)
                || new FileInfo(source).Length != new FileInfo(destination).Length)
                return false;
            using var sourceStream = File.OpenRead(source);
            using var destinationStream = File.OpenRead(destination);
            return System.Security.Cryptography.SHA256.HashData(sourceStream)
                .SequenceEqual(System.Security.Cryptography.SHA256.HashData(destinationStream));
        });
    }

    private static void CopyPublishedFiles(string sourceDirectory, string baseName)
    {
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, $"{baseName}.*"))
        {
            var destination = Path.Combine(InstallDirectory, Path.GetFileName(source));
            if (Path.GetFullPath(source).Equals(
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(source, destination, true);
        }
    }

    private static bool FirewallRuleMatches(
        object rules,
        string name,
        int protocol,
        int localPort,
        string localAddress,
        string remoteAddress)
    {
        object? existing = null;
        try
        {
            existing = ((dynamic)rules).Item(name);
            dynamic configured = existing;
            return configured.Enabled
                && (int)configured.Protocol == protocol
                && (int)configured.Direction == InboundDirection
                && (int)configured.Action == AllowAction
                && ((int)configured.Profiles & 7) == 7
                && string.Equals(
                    (string)configured.LocalPorts,
                    localPort.ToString(),
                    StringComparison.Ordinal)
                && AddressListContains((string)configured.LocalAddresses, localAddress)
                && AddressListContains((string)configured.RemoteAddresses, remoteAddress);
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(existing);
        }
    }

    private static bool AddressListContains(string? addresses, string expected) =>
        addresses?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => ScopesEquivalent(candidate, expected)) == true;

    private static bool ScopesEquivalent(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            return true;
        return TryParseScope(first, out var firstNetwork, out var firstPrefix)
            && TryParseScope(second, out var secondNetwork, out var secondPrefix)
            && firstNetwork == secondNetwork
            && firstPrefix == secondPrefix;
    }

    private static bool TryParseScope(string value, out uint network, out int prefixLength)
    {
        network = 0;
        prefixLength = 0;
        var components = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(components[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        prefixLength = 32;
        if (components.Length == 2)
        {
            if (!int.TryParse(components[1], out prefixLength))
            {
                if (!IPAddress.TryParse(components[1], out var maskAddress)
                    || maskAddress.AddressFamily != AddressFamily.InterNetwork)
                    return false;
                var maskValue = ToUInt(maskAddress);
                prefixLength = System.Numerics.BitOperations.PopCount(maskValue);
                var expectedMask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
                if (maskValue != expectedMask)
                    return false;
            }
            if (prefixLength is < 0 or > 32)
                return false;
        }
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        network = ToUInt(address) & mask;
        return true;
    }

    private static string NetworkCidr(NetworkChoice network)
    {
        var address = IPAddress.Parse(network.Address);
        if (address.AddressFamily != AddressFamily.InterNetwork
            || network.PrefixLength is < 1 or > 32)
            throw new InvalidOperationException("The selected adapter does not have a safe IPv4 subnet.");
        var value = ToUInt(address);
        var mask = network.PrefixLength == 32
            ? uint.MaxValue
            : uint.MaxValue << (32 - network.PrefixLength);
        return $"{FromUInt(value & mask)}/{network.PrefixLength}";
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static IPAddress FromUInt(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return new IPAddress(bytes);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private sealed record AgentState(
        int SchemaVersion,
        string EndpointId,
        string AdapterId,
        string AdapterName,
        string Address,
        int PrefixLength,
        DateTimeOffset InstalledUtc,
        DateTimeOffset UpdatedUtc,
        IReadOnlyList<AgentMembershipState> Memberships,
        AgentMulticastState? Multicast = null);

    private sealed record AgentMembershipState(
        string ServerAddress,
        string BaseUri,
        string JobName,
        DateTimeOffset RegisteredUtc);

    private sealed record AgentMulticastState(
        string JobName,
        DateTimeOffset UpdatedUtc,
        string? NetPrefix = null,
        string? Netmask = null,
        int? Ttl = null,
        bool SendEnabled = true,
        bool ReceiveEnabled = true);
}
