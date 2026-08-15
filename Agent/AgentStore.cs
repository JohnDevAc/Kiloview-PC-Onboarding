using System.Text.Json;

namespace KiloviewPcAgent;

internal static class AgentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NDI Configurator",
        "PC Agent");
    private static readonly string LegacyStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kiloview",
        "PC Agent",
        "agent-state.json");
    private static string StatePath => Environment.GetEnvironmentVariable(
        "KILOVIEW_AGENT_STATE_PATH") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(DirectoryPath, "agent-state.json");

    public static AgentConfiguration? Read()
    {
        try
        {
            var path = File.Exists(StatePath)
                ? StatePath
                : Environment.GetEnvironmentVariable("KILOVIEW_AGENT_STATE_PATH") is null
                    && File.Exists(LegacyStatePath)
                        ? LegacyStatePath
                        : null;
            if (path is null)
                return null;
            return JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void RemoveMembership(string serverAddress)
    {
        using var mutex = new Mutex(false, "Local\\KiloviewPcAgentState");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            throw new IOException("The PC Agent configuration is currently in use.");
        try
        {
            var current = Read() ?? throw new InvalidOperationException("The PC Agent is not configured.");
            var memberships = current.Memberships
                .Where(item => !string.Equals(
                    item.ServerAddress,
                    serverAddress,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Write(current with
            {
                UpdatedUtc = DateTimeOffset.UtcNow,
                Memberships = memberships,
                Multicast = current.Multicast is not null
                    && memberships.Any(item => string.Equals(
                        item.JobName,
                        current.Multicast.JobName,
                        StringComparison.Ordinal))
                            ? current.Multicast
                            : null
            });
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static void SetMulticastAssociation(AgentMulticastAssociation? association)
    {
        using var mutex = new Mutex(false, "Local\\KiloviewPcAgentState");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            throw new IOException("The PC Agent configuration is currently in use.");
        try
        {
            var current = Read() ?? throw new InvalidOperationException("The PC Agent is not configured.");
            var normalizedJob = association?.JobName.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedJob)
                && !current.Memberships.Any(item => string.Equals(
                    item.JobName,
                    normalizedJob,
                    StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "Managed multicast can only be associated with an existing job membership.");
            Write(current with
            {
                UpdatedUtc = DateTimeOffset.UtcNow,
                Multicast = string.IsNullOrWhiteSpace(normalizedJob)
                    ? null
                    : association! with
                    {
                        JobName = normalizedJob,
                        UpdatedUtc = DateTimeOffset.UtcNow
                    }
            });
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void Write(AgentConfiguration state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)
            ?? throw new InvalidOperationException("The PC Agent state directory could not be resolved."));
        var temporaryPath = StatePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, Json));
        File.Move(temporaryPath, StatePath, true);
    }
}
