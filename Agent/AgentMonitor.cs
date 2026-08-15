using Microsoft.Win32;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;

namespace KiloviewPcAgent;

internal static partial class AgentMonitor
{
    public static object Snapshot(AgentConfiguration configuration, DateTimeOffset agentStartedUtc)
    {
        configuration = AgentStore.Read() ?? configuration;
        var ndi = FindNdiTools();
        var drive = DriveInfo.GetDrives().FirstOrDefault(item =>
            item.IsReady && string.Equals(
                item.Name,
                Path.GetPathRoot(Environment.SystemDirectory),
                StringComparison.OrdinalIgnoreCase));
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var hasMemory = GlobalMemoryStatusEx(ref memory);
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            string.Equals(item.Id, configuration.AdapterId, StringComparison.OrdinalIgnoreCase));
        var networkProperties = network?.GetIPProperties();
        bool? dhcpEnabled = null;
        try { dhcpEnabled = networkProperties?.GetIPv4Properties().IsDhcpEnabled; }
        catch (NetworkInformationException) { }
        return new
        {
            schemaVersion = 1,
            product = "NDI Configurator PC Agent",
            agentVersion = Version(),
            status = "online",
            endpointId = configuration.EndpointId,
            hostname = Environment.MachineName,
            operatingSystemVersion = RuntimeInformation.OSDescription.Trim(),
            address = configuration.Address,
            prefixLength = configuration.PrefixLength,
            adapterId = configuration.AdapterId,
            adapterName = configuration.AdapterName,
            networkConfiguration = new
            {
                dhcpEnabled,
                defaultGateways = networkProperties?.GatewayAddresses
                    .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => item.Address.ToString())
                    .ToArray() ?? [],
                dnsServers = networkProperties?.DnsAddresses
                    .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => item.ToString())
                    .ToArray() ?? []
            },
            multicastConfiguration = AgentMulticastService.Current(configuration),
            ndiToolsInstalled = ndi.Installed,
            ndiToolsVersion = ndi.Version,
            agentStartedUtc,
            agentUptimeSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - agentStartedUtc).TotalSeconds),
            machineUptimeSeconds = Environment.TickCount64 / 1000,
            physicalMemoryTotalBytes = hasMemory ? memory.TotalPhysical : (ulong?)null,
            physicalMemoryAvailableBytes = hasMemory ? memory.AvailablePhysical : (ulong?)null,
            systemDriveTotalBytes = drive?.TotalSize,
            systemDriveFreeBytes = drive?.AvailableFreeSpace,
            memberships = configuration.Memberships.Select(item => new
            {
                serverAddress = item.ServerAddress,
                configuratorUrl = item.BaseUri,
                jobName = item.JobName,
                registeredUtc = item.RegisteredUtc
            }).ToArray(),
            observedUtc = DateTimeOffset.UtcNow
        };
    }

    public static NdiSnapshot FindNdiTools()
    {
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        };
        Version? best = null;
        var found = false;
        foreach (var (hive, view) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                    continue;
                foreach (var subkeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subkeyName);
                    var name = entry?.GetValue("DisplayName") as string;
                    var publisher = entry?.GetValue("Publisher") as string;
                    if (name is null
                        || !name.Contains("NDI", StringComparison.OrdinalIgnoreCase)
                        || !name.Contains("Tools", StringComparison.OrdinalIgnoreCase)
                        || publisher is null
                        || !PublisherPattern().IsMatch(publisher))
                        continue;
                    found = true;
                    var match = VersionPattern().Match(entry?.GetValue("DisplayVersion") as string ?? "");
                    if (match.Success && System.Version.TryParse(match.Value, out var parsed)
                        && (best is null || parsed > best))
                        best = parsed;
                }
            }
            catch (Exception ex) when (ex is SecurityException
                or UnauthorizedAccessException
                or IOException)
            {
                // Monitoring remains available when an uninstall registry key is unavailable.
            }
        }
        return new(found, best?.ToString());
    }

    public static string Version()
    {
        var assembly = typeof(AgentMonitor).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }

    [GeneratedRegex(@"(?i)(Vizrt|NDI|NewTek)")]
    private static partial Regex PublisherPattern();

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionPattern();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
