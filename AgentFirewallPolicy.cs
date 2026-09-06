namespace KiloviewPcOnboarding;

// One policy supplies both the COM writer and the read-back verifier.
internal sealed record AgentFirewallPolicy(int Protocol, int Port, string InterfaceName, string ApplicationPath)
{
    internal void Apply(object rule)
    {
        dynamic value = rule;
        value.Protocol = Protocol;
        value.LocalPorts = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        value.Direction = 1;
        value.Action = 1;
        value.Profiles = int.MaxValue;
        value.LocalAddresses = "*";
        value.RemoteAddresses = "LocalSubnet";
        value.Interfaces = new[] { InterfaceName };
        value.ApplicationName = ApplicationPath;
        value.EdgeTraversal = false;
        value.Enabled = true;
    }

    internal bool Matches(object rule)
    {
        try
        {
            dynamic value = rule;
            var interfaces = ((Array)value.Interfaces).Cast<object>().Select(item => item.ToString()?.Trim()).ToArray();
            return (bool)value.Enabled && !(bool)value.EdgeTraversal
                && (int)value.Protocol == Protocol && (int)value.Direction == 1 && (int)value.Action == 1
                && ((int)value.Profiles & 7) == 7
                && ((string)value.LocalPorts).Trim() == Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && ((string)value.LocalAddresses).Trim() == "*"
                && string.Equals(((string)value.RemoteAddresses).Trim(), "LocalSubnet", StringComparison.OrdinalIgnoreCase)
                && interfaces.Length == 1 && string.Equals(interfaces[0], InterfaceName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFullPath((string)value.ApplicationName), Path.GetFullPath(ApplicationPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
