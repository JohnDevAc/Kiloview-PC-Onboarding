namespace KiloviewPcAgent;

internal sealed record AgentConfiguration(
    int SchemaVersion,
    string EndpointId,
    string AdapterId,
    string AdapterName,
    string Address,
    int PrefixLength,
    DateTimeOffset InstalledUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<AgentMembership> Memberships);

internal sealed record AgentMembership(
    string ServerAddress,
    string BaseUri,
    string JobName,
    DateTimeOffset RegisteredUtc);

internal sealed record OnboardingLaunchRequest(
    string? ServerName,
    string? ServerAddress,
    string? JobName,
    string? ConfiguratorUrl,
    string RemoteAddress);

internal sealed record NdiSnapshot(bool Installed, string? Version);
