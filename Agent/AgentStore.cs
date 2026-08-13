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
        "Kiloview",
        "PC Agent");
    private static readonly string StatePath = Path.Combine(DirectoryPath, "agent-state.json");

    public static AgentConfiguration? Read()
    {
        try
        {
            if (!File.Exists(StatePath))
                return null;
            return JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(StatePath), Json);
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
            Write(current with
            {
                UpdatedUtc = DateTimeOffset.UtcNow,
                Memberships = current.Memberships
                    .Where(item => !string.Equals(
                        item.ServerAddress,
                        serverAddress,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            });
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void Write(AgentConfiguration state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = StatePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, Json));
        File.Move(temporaryPath, StatePath, true);
    }
}
