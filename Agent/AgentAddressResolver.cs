using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KiloviewPcAgent;

internal static class AgentAddressResolver
{
    internal static AgentConfiguration? Resolve(AgentConfiguration state)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id.Equals(state.AdapterId, StringComparison.OrdinalIgnoreCase)
            && n.OperationalStatus == OperationalStatus.Up);
        if (adapter is null) return null;
        var candidates = adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
            && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred
            && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => (Address: a.Address.ToString(), Prefix: a.PrefixLength)).ToArray();
        return Select(state, candidates);
    }

    internal static AgentConfiguration? Select(AgentConfiguration state, IReadOnlyList<(string Address, int Prefix)> candidates)
    {
        var exact = candidates.Where(a => a.Address == state.Address).ToArray();
        var selected = exact.Length == 1 ? exact : candidates.Count == 1 ? candidates.ToArray() : [];
        return selected.Length == 1 ? state with { Address = selected[0].Address, PrefixLength = selected[0].Prefix } : null;
    }
}
