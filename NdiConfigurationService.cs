using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiloviewPcOnboarding;

internal static class NdiConfigurationService
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static string ConfigPath => Environment.GetEnvironmentVariable(
        "KILOVIEW_NDI_CONFIG_PATH") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NDI",
                "ndi-config.v1.json");
    private static string DiscoveryUiConfigPath => Environment.GetEnvironmentVariable(
        "KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NDI",
                "Application.NDI.DiscoveryService.UI",
                "discovery_service_settings.json");

    public static async Task ApplyAsync(
        NetworkChoice network,
        JobConfiguratorInstance server,
        CancellationToken ct)
    {
        EnsureApplicationsClosed();
        await using var configurationLock = await AcquireConfigurationLockAsync(ct);
        await ApplyConfigurationFilesAsync(network, server, ct);
    }

    internal static void EnsureApplicationsClosed()
    {
        if (IsAccessManagerRunning() || IsDiscoveryRunning())
            throw new InvalidOperationException(
                "Close NDI Access Manager and NDI Discovery before applying settings. "
                + "Either application can overwrite externally applied settings when it exits.");
    }

    internal static async Task PreflightAsync(CancellationToken ct)
    {
        EnsureApplicationsClosed();
        _ = await ReadConfigurationAsync(ConfigPath, "NDI Access Manager", ct);
        _ = await ReadConfigurationAsync(DiscoveryUiConfigPath, "NDI Discovery", ct);
    }

    internal static async Task ApplyConfigurationFilesAsync(
        NetworkChoice network,
        JobConfiguratorInstance server,
        CancellationToken ct)
    {
        var configPath = ConfigPath;
        JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                await using var input = File.OpenRead(configPath);
                root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject
                    ?? throw new JsonException("The root value is not an object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The NDI configuration at '{configPath}' is invalid. Open NDI Access Manager once to repair it.",
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

        await WriteConfigurationAsync(configPath, root, ct);

        var discoveryPath = DiscoveryUiConfigPath;
        var discoveryRoot = await ReadConfigurationAsync(discoveryPath, "NDI Discovery", ct);
        discoveryRoot["isNotFirstRun"] = true;
        var discovery = Object(discoveryRoot, "discoverySettingsModel");
        discovery["ipaddressstring"] = server.NdiDiscoveryServerIp;
        discovery["useaccessmanagersettings"] = true;
        discovery["uselocalhost"] = false;
        await WriteConfigurationAsync(discoveryPath, discoveryRoot, ct);

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

        await using var discoveryInput = File.OpenRead(DiscoveryUiConfigPath);
        var discoveryRoot = await JsonNode.ParseAsync(discoveryInput, cancellationToken: ct) as JsonObject;
        var discovery = discoveryRoot?["discoverySettingsModel"] as JsonObject;
        if (!Bool(discovery, "useaccessmanagersettings")
            || !string.Equals(
                Text(discovery, "ipaddressstring"),
                discoveryServer,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "NDI Discovery did not retain the Access Manager discovery-server setting.");
    }

    private static bool IsAccessManagerRunning() => IsAnyProcessRunning(
        "Application.NdiGroupEditor",
        "Access Manager",
        "NDI Access Manager");

    private static bool IsDiscoveryRunning() => IsAnyProcessRunning(
        "Application.NDI.DiscoveryService.UI",
        "NDI Discovery Service");

    private static bool IsAnyProcessRunning(params string[] names) => names.Any(name =>
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private static async Task<JsonObject> ReadConfigurationAsync(
        string path,
        string product,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            await using var input = File.OpenRead(path);
            return await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject
                ?? throw new JsonException("The root value is not an object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The {product} configuration at '{path}' is invalid. Open {product} once to repair it.",
                ex);
        }
    }

    private static async Task WriteConfigurationAsync(
        string path,
        JsonObject root,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The NDI configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Copy(path, path + ".kiloview-pc-onboarding-backup", true);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                root.ToJsonString(Indented) + Environment.NewLine,
                ct);
            await using (var verify = File.OpenRead(temporary))
                _ = await JsonNode.ParseAsync(verify, cancellationToken: ct)
                    ?? throw new InvalidOperationException("The generated NDI configuration could not be verified.");
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<FileStream> AcquireConfigurationLockAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException("The NDI configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, ".kiloview-ndi-configuration.lock");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "The NDI configuration is currently being updated. Retry shortly.",
                    ex);
            }
        }
    }

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

    private static bool Bool(JsonObject? parent, string name) =>
        parent?[name] is JsonValue value
        && (value.TryGetValue<bool>(out var result) && result
            || value.TryGetValue<string>(out var text) && bool.TryParse(text, out result) && result);
}
