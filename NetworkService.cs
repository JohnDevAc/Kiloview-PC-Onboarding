using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KiloviewPcOnboarding;

internal static class NetworkService
{
    public static IReadOnlyList<NetworkChoice> GetChoices() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up
            && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            && network.NetworkInterfaceType is not NetworkInterfaceType.Tunnel)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address.Address))
            .Select(address => new NetworkChoice(
                network.Id,
                network.Name,
                network.Description,
                address.Address.ToString(),
                address.PrefixLength)))
        .DistinctBy(network => (network.Id, network.Address))
        .OrderBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(network => network.Address, StringComparer.Ordinal)
        .ToArray();

    public static IEnumerable<IPAddress> ScanAddresses(NetworkChoice choice)
    {
        var address = IPAddress.Parse(choice.Address);
        var prefix = Math.Clamp(choice.PrefixLength, 24, 30);
        var mask = uint.MaxValue << (32 - prefix);
        var network = ToUInt(address) & mask;
        var broadcast = network | ~mask;
        for (var current = network + 1; current < broadcast; current++)
            yield return FromUInt(current);
    }

    public static HttpClient CreateBoundClient(NetworkChoice choice, TimeSpan timeout)
    {
        var localAddress = IPAddress.Parse(choice.Address);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(700),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 2,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static IPAddress FromUInt(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return new IPAddress(bytes);
    }
}
