using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace KiloviewPcOnboarding;

internal sealed record FirewallRuleResult(
    bool Applied,
    string RemoteScope,
    string? Warning);

internal static class FirewallService
{
    internal const string RuleName = "Kiloview PC Onboarding - ICMPv4 Echo";
    private const int IcmpV4Protocol = 1;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int PrivateProfile = 2;

    public static FirewallRuleResult EnsurePingRule(
        NetworkChoice network,
        JobConfiguratorInstance server)
    {
        var remoteScope = ResolveRemoteScope(network, server);
        if (!IsAdministrator())
        {
            return new(
                false,
                remoteScope,
                "Administrator elevation is required to configure the Windows Firewall ping rule.");
        }

        object? policy = null;
        object? rules = null;
        object? rule = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new InvalidOperationException("Windows Firewall is not available.");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
                ?? throw new InvalidOperationException("Windows Firewall rule management is not available.");

            policy = Activator.CreateInstance(policyType)
                ?? throw new InvalidOperationException("Windows Firewall could not be opened.");
            rules = ((dynamic)policy).Rules;
            rule = Activator.CreateInstance(ruleType)
                ?? throw new InvalidOperationException("The Windows Firewall rule could not be created.");

            dynamic configuredRule = rule;
            configuredRule.Name = RuleName;
            configuredRule.Description =
                "Allows the selected Kiloview Job Configurator to check whether this onboarded PC is online.";
            configuredRule.Protocol = IcmpV4Protocol;
            configuredRule.IcmpTypesAndCodes = "8:*";
            configuredRule.Direction = InboundDirection;
            configuredRule.Action = AllowAction;
            configuredRule.Profiles = PrivateProfile;
            configuredRule.RemoteAddresses = remoteScope;
            configuredRule.LocalAddresses = network.Address;
            configuredRule.EdgeTraversal = false;
            configuredRule.Enabled = true;

            // Add replaces an existing rule with the same identifier. Building a
            // detached rule first avoids the Firewall API validating and
            // committing every individual property change on an existing rule.
            ((dynamic)rules).Add(configuredRule);
            ReleaseComObject(rule);
            rule = null;

            if (!RuleMatches(rules, network.Address, remoteScope))
                throw new InvalidOperationException(
                    "Windows did not retain the required Private-profile ICMPv4 rule settings.");

            return new(true, remoteScope, null);
        }
        catch (Exception ex)
        {
            if (RuleMatches(rules, network.Address, remoteScope))
                return new(true, remoteScope, null);

            return new(
                false,
                remoteScope,
                "The Private-profile ICMPv4 Echo Request rule could not be applied. "
                + $"Job Configurator may show this PC as unavailable. {ex.Message}");
        }
        finally
        {
            ReleaseComObject(rule);
            ReleaseComObject(rules);
            ReleaseComObject(policy);
        }
    }

    internal static string ResolveRemoteScope(
        NetworkChoice network,
        JobConfiguratorInstance server)
    {
        foreach (var candidate in new[] { server.Address, server.BaseUri.Host })
        {
            if (IPAddress.TryParse(candidate, out var address)
                && address.AddressFamily == AddressFamily.InterNetwork
                && !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.Broadcast))
                return address.ToString();
        }

        var localAddress = IPAddress.Parse(network.Address);
        if (localAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The selected adapter does not have an IPv4 address.");
        if (network.PrefixLength is <= 0 or > 32)
            throw new InvalidOperationException(
                "The selected adapter subnet cannot be used as a safe firewall scope.");

        var addressValue = ToUInt(localAddress);
        var mask = network.PrefixLength == 32
            ? uint.MaxValue
            : uint.MaxValue << (32 - network.PrefixLength);
        return $"{FromUInt(addressValue & mask)}/{network.PrefixLength}";
    }

    private static bool RuleMatches(
        object? rules,
        string localAddress,
        string remoteScope)
    {
        if (rules is null)
            return false;

        object? existing = null;
        try
        {
            existing = ((dynamic)rules).Item(RuleName);
            dynamic configuredRule = existing;
            return configuredRule.Enabled
                && (int)configuredRule.Protocol == IcmpV4Protocol
                && (int)configuredRule.Direction == InboundDirection
                && (int)configuredRule.Action == AllowAction
                && ((int)configuredRule.Profiles & PrivateProfile) == PrivateProfile
                && AddressListContains((string)configuredRule.LocalAddresses, localAddress)
                && AddressListContains((string)configuredRule.RemoteAddresses, remoteScope)
                && string.Equals(
                    (string)configuredRule.IcmpTypesAndCodes,
                    "8:*",
                    StringComparison.OrdinalIgnoreCase);
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
                var expectedMask = prefixLength == 0
                    ? 0u
                    : uint.MaxValue << (32 - prefixLength);
                if (maskValue != expectedMask)
                    return false;
            }

            if (prefixLength is < 0 or > 32)
                return false;
        }

        var mask = prefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - prefixLength);
        network = ToUInt(address) & mask;
        return true;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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
}
