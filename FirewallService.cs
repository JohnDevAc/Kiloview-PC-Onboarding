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
    private const string ProductGroup = "Kiloview PC Onboarding";
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
            rule = FindRule(rules) ?? Activator.CreateInstance(ruleType)
                ?? throw new InvalidOperationException("The Windows Firewall rule could not be created.");
            var isNew = string.IsNullOrWhiteSpace(((dynamic)rule).Name as string);

            dynamic configuredRule = rule;
            configuredRule.Name = RuleName;
            configuredRule.Description =
                "Allows the selected Kiloview Job Configurator to check whether this onboarded PC is online.";
            configuredRule.Grouping = ProductGroup;
            configuredRule.Protocol = IcmpV4Protocol;
            configuredRule.IcmpTypesAndCodes = "8:*";
            configuredRule.Direction = InboundDirection;
            configuredRule.Action = AllowAction;
            configuredRule.Profiles = PrivateProfile;
            configuredRule.RemoteAddresses = remoteScope;
            configuredRule.LocalAddresses = network.Address;
            configuredRule.EdgeTraversal = false;
            configuredRule.Enabled = true;

            if (isNew)
                ((dynamic)rules).Add(configuredRule);

            return new(true, remoteScope, null);
        }
        catch (Exception ex)
        {
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

    private static object? FindRule(object rules)
    {
        try
        {
            return ((dynamic)rules).Item(RuleName);
        }
        catch (COMException)
        {
            return null;
        }
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
