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
    IReadOnlyList<AgentMembership> Memberships,
    AgentMulticastAssociation? Multicast = null);

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

internal sealed record AgentMulticastAssociation(
    string JobName,
    DateTimeOffset UpdatedUtc,
    string? NetPrefix = null,
    string? Netmask = null,
    int? Ttl = null,
    bool SendEnabled = true,
    bool ReceiveEnabled = true);

internal sealed record MulticastConfigurationRequest(
    int SchemaVersion,
    string EndpointId,
    string JobName,
    string AdapterId,
    string Mode,
    bool SendEnabled,
    bool ReceiveEnabled,
    string? NetPrefix,
    string? Netmask,
    int? Ttl);

internal sealed record MulticastConfigurationState(
    int SchemaVersion,
    string Product,
    string EndpointId,
    string Mode,
    string AdapterId,
    bool SendEnabled,
    bool ReceiveEnabled,
    string? NetPrefix,
    string? Netmask,
    int? Ttl,
    string? JobName,
    bool InUse,
    DateTimeOffset ObservedUtc);
