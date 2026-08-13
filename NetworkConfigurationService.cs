using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KiloviewPcOnboarding;

internal static class NetworkConfigurationService
{
    internal static NetworkConfigurationPlan CreatePlan(
        NetworkChoice current,
        RemoteNetworkConfiguration? requested,
        string serverAddress)
    {
        if (!TryIpv4(serverAddress, out var server))
            throw new InvalidOperationException("The requesting Configurator address is not valid IPv4.");
        if (requested is null)
            return new(current, "unchanged", null, null, null, null);
        if (!string.Equals(requested.AdapterId, current.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The remote network configuration does not target the agent's selected adapter.");

        if (string.IsNullOrWhiteSpace(requested.Mode))
            throw new InvalidOperationException(
                "The remote network mode must be unchanged, dhcp, or static.");
        var mode = requested.Mode.Trim().ToLowerInvariant();
        if (mode == "unchanged")
            return new(current, mode, null, null, null, null);
        if (mode == "dhcp")
            return new(current, mode, null, null, null, null);
        if (mode != "static")
            throw new InvalidOperationException(
                "The remote network mode must be unchanged, dhcp, or static.");

        if (!TryIpv4(requested.Address, out var address)
            || requested.PrefixLength is not int prefix
            || prefix is < 1 or > 30)
            throw new InvalidOperationException(
                "Static network configuration requires a usable IPv4 address and prefix length from 1 to 30.");
        EnsureUsableHost(address, prefix, "Static IPv4 address");
        if (!SameSubnet(address, server, prefix))
            throw new InvalidOperationException(
                "The static IPv4 address would place the PC outside the requesting Configurator's subnet.");

        string? gatewayText = null;
        if (!string.IsNullOrWhiteSpace(requested.DefaultGateway))
        {
            if (!TryIpv4(requested.DefaultGateway, out var gateway))
                throw new InvalidOperationException("The default gateway is not valid IPv4.");
            EnsureUsableHost(gateway, prefix, "Default gateway");
            if (!SameSubnet(address, gateway, prefix))
                throw new InvalidOperationException(
                    "The default gateway is outside the configured static subnet.");
            gatewayText = gateway.ToString();
        }

        IReadOnlyList<string>? dnsServers = null;
        if (requested.DnsServers is not null)
        {
            dnsServers = requested.DnsServers.Select(value =>
            {
                if (!TryIpv4(value, out var dns)
                    || dns.Equals(IPAddress.Any)
                    || dns.Equals(IPAddress.Broadcast)
                    || IsMulticast(dns))
                    throw new InvalidOperationException($"DNS server '{value}' is not usable IPv4.");
                return dns.ToString();
            }).Distinct(StringComparer.Ordinal).ToArray();
        }

        return new(
            current,
            mode,
            address.ToString(),
            prefix,
            gatewayText,
            dnsServers);
    }

    public static async Task<NetworkChoice> ApplyAsync(
        NetworkConfigurationPlan plan,
        string serverAddress,
        CancellationToken ct)
    {
        if (!plan.ChangesNetwork)
            return plan.Current;

        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            string.Equals(item.Id, plan.Current.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected network adapter is no longer available.");
        if (string.Equals(plan.Mode, "dhcp", StringComparison.Ordinal))
        {
            await RunNetshAsync(
                ["interface", "ipv4", "set", "address", $"name={adapter.Name}", "source=dhcp"],
                ct);
            await RunNetshAsync(
                ["interface", "ipv4", "set", "dnsservers", $"name={adapter.Name}", "source=dhcp"],
                ct);
            var configured = await WaitForAddressAsync(adapter.Id, null, ct);
            EnsureServerReachableSubnet(configured, serverAddress);
            return configured;
        }

        var address = plan.Address
            ?? throw new InvalidOperationException("The static IPv4 address is missing.");
        var prefix = plan.PrefixLength
            ?? throw new InvalidOperationException("The static prefix length is missing.");
        var addressArguments = new List<string>
        {
            "interface", "ipv4", "set", "address",
            $"name={adapter.Name}",
            "source=static",
            $"address={address}",
            $"mask={PrefixMask(prefix)}",
            $"gateway={plan.DefaultGateway ?? "none"}"
        };
        if (plan.DefaultGateway is not null)
            addressArguments.Add("gwmetric=1");
        addressArguments.Add("store=persistent");
        await RunNetshAsync(addressArguments, ct);

        if (plan.DnsServers is not null)
        {
            var first = plan.DnsServers.FirstOrDefault() ?? "none";
            await RunNetshAsync(
            [
                "interface", "ipv4", "set", "dnsservers",
                $"name={adapter.Name}",
                "source=static",
                $"address={first}",
                "validate=no"
            ], ct);
            for (var index = 1; index < plan.DnsServers.Count; index++)
            {
                await RunNetshAsync(
                [
                    "interface", "ipv4", "add", "dnsservers",
                    $"name={adapter.Name}",
                    $"address={plan.DnsServers[index]}",
                    $"index={index + 1}",
                    "validate=no"
                ], ct);
            }
        }

        var result = await WaitForAddressAsync(adapter.Id, address, ct);
        EnsureServerReachableSubnet(result, serverAddress);
        return result;
    }

    private static async Task<NetworkChoice> WaitForAddressAsync(
        string adapterId,
        string? expectedAddress,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                    string.Equals(item.Id, adapterId, StringComparison.OrdinalIgnoreCase));
                if (network is not null)
                {
                    var candidate = network.GetIPProperties().UnicastAddresses
                        .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(item.Address)
                            && !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        .FirstOrDefault(item => expectedAddress is null
                            || string.Equals(item.Address.ToString(), expectedAddress, StringComparison.Ordinal));
                    if (candidate is not null)
                        return new(
                            network.Id,
                            network.Name,
                            network.Description,
                            candidate.Address.ToString(),
                            candidate.PrefixLength);
                }
                await Task.Delay(500, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                expectedAddress is null
                    ? "The adapter did not obtain a DHCP IPv4 address within 30 seconds."
                    : $"The adapter did not apply IPv4 address {expectedAddress} within 30 seconds.");
        }
    }

    private static async Task RunNetshAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows could not start network configuration.");
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows network configuration failed ({process.ExitCode}): "
                + string.Join(" ", new[] { error.Trim(), output.Trim() }
                    .Where(value => value.Length > 0)));
    }

    private static void EnsureServerReachableSubnet(NetworkChoice network, string serverAddress)
    {
        if (!TryIpv4(network.Address, out var local)
            || !TryIpv4(serverAddress, out var server)
            || !SameSubnet(local, server, network.PrefixLength))
            throw new InvalidOperationException(
                "The applied network settings place the PC outside the requesting Configurator's subnet.");
    }

    private static void EnsureUsableHost(IPAddress address, int prefix, string label)
    {
        if (address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast)
            || IPAddress.IsLoopback(address)
            || IsMulticast(address))
            throw new InvalidOperationException($"{label} is not a usable unicast address.");
        var value = ToUInt(address);
        var mask = uint.MaxValue << (32 - prefix);
        if (value == (value & mask) || value == ((value & mask) | ~mask))
            throw new InvalidOperationException($"{label} cannot be the subnet or broadcast address.");
    }

    private static bool IsMulticast(IPAddress address) => address.GetAddressBytes()[0] is >= 224 and <= 239;

    private static bool TryIpv4(string? value, out IPAddress address) =>
        IPAddress.TryParse(value, out address!)
        && address.AddressFamily == AddressFamily.InterNetwork;

    private static bool SameSubnet(IPAddress first, IPAddress second, int prefix)
    {
        if (prefix is < 1 or > 32)
            return false;
        var mask = prefix == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
        return (ToUInt(first) & mask) == (ToUInt(second) & mask);
    }

    private static string PrefixMask(int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return FromUInt(mask).ToString();
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
}
