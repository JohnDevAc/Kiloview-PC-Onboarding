using System.Text.Json;

namespace KiloviewPcOnboarding;

internal static class ConsentStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NDI Configurator PC Agent");
    private static readonly string LegacyDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Kiloview PC Onboarding");
    private static readonly string ConsentPath = Path.Combine(DirectoryPath, "consent.json");
    private static readonly string EndpointPath = Path.Combine(DirectoryPath, "endpoint-id.txt");
    private static readonly string LegacyConsentPath = Path.Combine(LegacyDirectoryPath, "consent.json");
    private static readonly string LegacyEndpointPath = Path.Combine(LegacyDirectoryPath, "endpoint-id.txt");

    public static bool IsAccepted(string version)
    {
        try
        {
            var path = File.Exists(ConsentPath)
                ? ConsentPath
                : LegacyConsentPath;
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var isAccepted = document.RootElement.TryGetProperty("eulaVersion", out var accepted)
                && string.Equals(accepted.GetString(), version, StringComparison.Ordinal);
            if (isAccepted
                && string.Equals(path, LegacyConsentPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(DirectoryPath);
                File.Copy(LegacyConsentPath, ConsentPath, overwrite: true);
            }
            return isAccepted;
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
        if (File.Exists(LegacyEndpointPath)
            && Guid.TryParse(File.ReadAllText(LegacyEndpointPath).Trim(), out var legacy))
        {
            var migrated = legacy.ToString("D");
            File.WriteAllText(EndpointPath, migrated);
            return migrated;
        }
        var created = Guid.NewGuid().ToString("D");
        File.WriteAllText(EndpointPath, created);
        return created;
    }
}
