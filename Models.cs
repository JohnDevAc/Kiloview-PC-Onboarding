namespace KiloviewPcOnboarding;

internal sealed record NetworkChoice(
    string Id,
    string Name,
    string Description,
    string Address,
    int PrefixLength)
{
    public override string ToString() => $"{Name} — {Address}/{PrefixLength} ({Description})";
}

internal sealed record NdiToolsStatus(
    bool Installed,
    Version? InstalledVersion,
    Version? CurrentVersion,
    string? AccessManagerPath,
    string Message)
{
    public bool UpdateRequired => !Installed
        || CurrentVersion is not null
        && (InstalledVersion is null || InstalledVersion < CurrentVersion);
}

internal sealed record JobConfiguratorInstance(
    string Address,
    Uri BaseUri,
    string Version,
    string Channel,
    string JobName,
    string NdiDiscoveryServerIp,
    bool SupportsRegistration,
    bool AlreadyOnboarded = false)
{
    public override string ToString()
    {
        var compatibility = SupportsRegistration ? "" : " · update required";
        var registration = AlreadyOnboarded ? " · already onboarded" : "";
        return $"{JobName} — {Address} · v{Version} {Channel}{compatibility}{registration}";
    }
}

internal sealed record RegistrationRequest(
    string EndpointId,
    string Hostname,
    string Address,
    string AdapterName,
    int PrefixLength,
    bool PreferredInterfaceConfigured,
    string NdiToolsVersion,
    string UtilityVersion,
    string EulaVersion);
