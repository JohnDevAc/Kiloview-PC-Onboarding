using System.Net;
using System.Net.Sockets;

namespace NdiSuite.Configuration;

internal static class AgentConfigurationValidity
{
    internal static bool IsValid(int schema, string? endpointId, string? adapterId, string? address, int prefix)
    {
        if (schema != 1 || !Guid.TryParse(endpointId, out var endpoint) || endpoint == Guid.Empty
            || !Guid.TryParse(adapterId, out var adapter) || adapter == Guid.Empty || prefix is < 1 or > 30
            || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254) return false;
        var hostMask = uint.MaxValue >> prefix;
        var number = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return (number & hostMask) != 0 && (number & hostMask) != hostMask;
    }
}
