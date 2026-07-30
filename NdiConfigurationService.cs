using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiloviewPcOnboarding;

internal static class NdiConfigurationService
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NDI",
        "ndi-config.v1.json");

    public static async Task ApplyAsync(
        NetworkChoice network,
        JobConfiguratorInstance server,
        CancellationToken ct)
    {
        if (IsAccessManagerRunning())
            throw new InvalidOperationException(
                "Close NDI Access Manager before onboarding. It can overwrite externally applied settings when it exits.");

        JsonObject root;
        if (File.Exists(ConfigPath))
        {
            try
            {
                await using var input = File.OpenRead(ConfigPath);
                root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject
                    ?? throw new JsonException("The root value is not an object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The NDI configuration at '{ConfigPath}' is invalid. Open NDI Access Manager once to repair it.",
                    ex);
            }
        }
        else root = new JsonObject();

        var ndi = Object(root, "ndi");
        var adapters = Object(ndi, "adapters");
        adapters["allowed"] = new JsonArray(network.Address);
        var groups = Object(ndi, "groups");
        groups["send"] = AddValue(Text(groups, "send"), server.JobName);
        groups["recv"] = AddValue(Text(groups, "recv"), server.JobName);
        Object(ndi, "networks")["discovery"] = server.NdiDiscoveryServerIp;

        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(ConfigPath))
            File.Copy(ConfigPath, ConfigPath + ".kiloview-pc-onboarding-backup", true);
        var temporary = Path.Combine(directory, $".ndi-config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, root.ToJsonString(Indented) + Environment.NewLine, ct);
            await using (var verify = File.OpenRead(temporary))
                _ = await JsonNode.ParseAsync(verify, cancellationToken: ct)
                    ?? throw new InvalidOperationException("The generated NDI configuration could not be verified.");
            File.Move(temporary, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        await VerifyAsync(network.Address, server.JobName, server.NdiDiscoveryServerIp, ct);
    }

    private static async Task VerifyAsync(string address, string group, string discoveryServer, CancellationToken ct)
    {
        await using var input = File.OpenRead(ConfigPath);
        var root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject;
        var allowed = root?["ndi"]?["adapters"]?["allowed"] as JsonArray;
        var groups = root?["ndi"]?["groups"] as JsonObject;
        var actualDiscovery = root?["ndi"]?["networks"]?["discovery"]?.GetValue<string>();
        var valid = allowed?.Count == 1
            && string.Equals(allowed[0]?.GetValue<string>(), address, StringComparison.Ordinal)
            && Contains(Text(groups, "send"), group)
            && Contains(Text(groups, "recv"), group)
            && Contains(actualDiscovery, discoveryServer);
        if (!valid) throw new InvalidOperationException("NDI Access Manager did not retain the onboarding settings.");
    }

    private static bool IsAccessManagerRunning() => new[] { "Access Manager", "NDI Access Manager" }.Any(name =>
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    });

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string AddValue(string? current, string value) => string.Join(
        ",",
        (current ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool Contains(string? current, string expected) =>
        (current ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expected.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string? Text(JsonObject? parent, string name) =>
        parent?[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
