using System.Text.Json;

namespace KiloviewPcOnboarding;

internal static class ConsentStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Kiloview PC Onboarding");
    private static readonly string ConsentPath = Path.Combine(DirectoryPath, "consent.json");
    private static readonly string EndpointPath = Path.Combine(DirectoryPath, "endpoint-id.txt");

    public static bool IsAccepted(string version)
    {
        try
        {
            if (!File.Exists(ConsentPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(ConsentPath));
            return document.RootElement.TryGetProperty("eulaVersion", out var accepted)
                && string.Equals(accepted.GetString(), version, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void Record(string version)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(ConsentPath, JsonSerializer.Serialize(new
        {
            eulaVersion = version,
            acceptedUtc = DateTimeOffset.UtcNow,
            acceptedBy = Environment.UserName
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string EndpointId()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(EndpointPath)
            && Guid.TryParse(File.ReadAllText(EndpointPath).Trim(), out var existing))
            return existing.ToString("D");
        var created = Guid.NewGuid().ToString("D");
        File.WriteAllText(EndpointPath, created);
        return created;
    }
}
