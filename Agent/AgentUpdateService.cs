using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiloviewPcAgent;

internal sealed record AgentUpdateRelease(
    string TagName,
    Version Version,
    Uri PackageUrl,
    Uri ChecksumUrl,
    string PackageDigest,
    long PackageSize);

internal sealed record AgentUpdateCheck(
    Version CurrentVersion,
    AgentUpdateRelease Release)
{
    public bool UpdateAvailable => Release.Version > CurrentVersion;
}

internal static class AgentUpdateService
{
    internal const string PackageAssetName = "NDI-Configurator-PC-Agent-win-x64.zip";
    internal const string ChecksumAssetName = PackageAssetName + ".sha256";
    private const string ProductName = "NDI Configurator PC Agent";
    private const long MaximumPackageBytes = 256L * 1024 * 1024;
    private const long MaximumExtractedBytes = 512L * 1024 * 1024;
    private const int MaximumArchiveEntries = 256;
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/JohnDevAc/Kiloview-PC-Onboarding/releases/latest");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<AgentUpdateCheck> CheckAsync(CancellationToken ct)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "The public update feed is not available. Confirm the GitHub repository is public and has a production release.");
        response.EnsureSuccessStatusCode();
        var payload = await ReadLimitedTextAsync(response, 1024 * 1024, ct);
        return ParseLatestRelease(payload, CurrentVersion());
    }

    public static async Task<string> DownloadAndStageAsync(
        AgentUpdateRelease release,
        CancellationToken ct)
    {
        ValidateRelease(release);
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NDI Configurator",
            "PC Agent",
            "Updates");
        Directory.CreateDirectory(updateRoot);
        CleanOldUpdates(updateRoot);

        var versionDirectory = Path.Combine(updateRoot, release.Version.ToString(3));
        var packagePath = Path.Combine(updateRoot, $"{release.TagName}.zip.partial");
        TryDeleteFile(packagePath);
        TryDeleteDirectory(versionDirectory);

        try
        {
            using var client = CreateClient();
            var checksumText = await DownloadTextAsync(
                client,
                release.ChecksumUrl,
                1024,
                ct);
            var manifestDigest = ParseChecksum(checksumText, PackageAssetName);
            if (!string.Equals(manifestDigest, release.PackageDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The release checksum does not match GitHub's package digest.");

            await DownloadFileAsync(
                client,
                release.PackageUrl,
                packagePath,
                release.PackageSize,
                ct);
            await using var packageStream = File.OpenRead(packagePath);
            var actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(
                packageStream,
                ct));
            if (!string.Equals(actualDigest, manifestDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The downloaded update package failed SHA-256 verification.");

            Directory.CreateDirectory(versionDirectory);
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                ValidateArchiveEntries(archive);
                ExtractArchive(archive, versionDirectory);
            }

            var setupPath = Path.Combine(
                versionDirectory,
                "NDI Configurator PC Agent Setup.exe");
            var agentPath = Path.Combine(
                versionDirectory,
                "Agent",
                "NDI Configurator PC Agent.exe");
            ValidatePayload(setupPath, release.Version, "setup");
            ValidatePayload(agentPath, release.Version, "agent");
            return setupPath;
        }
        catch
        {
            TryDeleteDirectory(versionDirectory);
            throw;
        }
        finally
        {
            TryDeleteFile(packagePath);
        }
    }

    internal static AgentUpdateCheck ParseLatestRelease(string payload, Version currentVersion)
    {
        GitHubRelease release;
        try
        {
            release = JsonSerializer.Deserialize<GitHubRelease>(payload, Json)
                ?? throw new JsonException("The response was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("GitHub returned invalid release metadata.", ex);
        }

        if (release.Draft || release.Prerelease)
            throw new InvalidOperationException("GitHub returned a non-production release.");
        if (!string.Equals(release.TargetCommitish, "main", StringComparison.Ordinal))
            throw new InvalidOperationException("The latest release was not deployed from the main branch.");
        var version = ParseVersion(release.TagName ?? "");
        var package = FindAsset(release.Assets, PackageAssetName);
        var checksum = FindAsset(release.Assets, ChecksumAssetName);
        var digest = ParseGitHubDigest(package.Digest);
        if (package.Size <= 0 || package.Size > MaximumPackageBytes)
            throw new InvalidOperationException("The release package size is invalid.");

        var result = new AgentUpdateRelease(
            release.TagName!,
            version,
            ParseAssetUri(package.DownloadUrl, PackageAssetName),
            ParseAssetUri(checksum.DownloadUrl, ChecksumAssetName),
            digest,
            package.Size);
        ValidateRelease(result);
        return new AgentUpdateCheck(NormalizeVersion(currentVersion), result);
    }

    internal static string ParseChecksum(string value, string expectedFileName)
    {
        var fields = value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2
            || fields[0].Length != 64
            || !fields[0].All(Uri.IsHexDigit)
            || !string.Equals(fields[1].TrimStart('*'), expectedFileName, StringComparison.Ordinal))
            throw new InvalidOperationException("The release checksum manifest is invalid.");
        return fields[0].ToUpperInvariant();
    }

    internal static void ValidateArchiveEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidOperationException("The update archive contains an invalid number of entries.");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (Path.IsPathRooted(entry.FullName)
                || entry.FullName.Contains(':')
                || entry.FullName.Split('/', '\\').Any(part => part == ".."))
                throw new InvalidOperationException("The update archive contains an unsafe path.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExtractedBytes)
                throw new InvalidOperationException("The update archive expands beyond the allowed size.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "NDI-Configurator-PC-Agent",
            AgentMonitor.Version()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static Version CurrentVersion()
    {
        var assemblyVersion = typeof(AgentUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return ParseInstalledVersion(assemblyVersion ?? AgentMonitor.Version());
    }

    internal static Version ParseInstalledVersion(string value)
    {
        var text = value.Trim();
        var suffix = text.IndexOfAny(['-', '+']);
        return ParseVersion(suffix < 0 ? text : text[..suffix]);
    }

    private static Version ParseVersion(string value)
    {
        var text = value.Trim().TrimStart('v', 'V');
        if (text.Contains('-') || text.Contains('+') || !Version.TryParse(text, out var parsed))
            throw new InvalidOperationException("The release version is invalid.");
        return NormalizeVersion(parsed);
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build));

    private static GitHubAsset FindAsset(IReadOnlyList<GitHubAsset>? assets, string name) =>
        assets?.SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"The latest release does not contain {name}.");

    private static string ParseGitHubDigest(string? value)
    {
        const string prefix = "sha256:";
        if (value is null
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 64
            || !value[prefix.Length..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("GitHub did not provide a valid SHA-256 package digest.");
        return value[prefix.Length..].ToUpperInvariant();
    }

    private static Uri ParseAssetUri(string? value, string assetName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(
                "/JohnDevAc/Kiloview-PC-Onboarding/releases/download/",
                StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith('/' + assetName, StringComparison.Ordinal))
            throw new InvalidOperationException($"The {assetName} download URL is invalid.");
        return uri;
    }

    private static void ValidateRelease(AgentUpdateRelease release)
    {
        _ = ParseAssetUri(release.PackageUrl.AbsoluteUri, PackageAssetName);
        _ = ParseAssetUri(release.ChecksumUrl.AbsoluteUri, ChecksumAssetName);
        if (release.PackageDigest.Length != 64 || !release.PackageDigest.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The update package digest is invalid.");
        if (release.PackageSize <= 0 || release.PackageSize > MaximumPackageBytes)
            throw new InvalidOperationException("The update package size is invalid.");
    }

    private static async Task<string> DownloadTextAsync(
        HttpClient client,
        Uri uri,
        int maximumBytes,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedTextAsync(response, maximumBytes, ct);
    }

    private static async Task<string> ReadLimitedTextAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidOperationException("The update service response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(Math.Min(maximumBytes, 8192));
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidOperationException("The update service response is too large.");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long expectedSize,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != expectedSize)
            throw new InvalidOperationException("The downloaded package size does not match the release metadata.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > expectedSize || total > MaximumPackageBytes)
                throw new InvalidOperationException("The downloaded update package is larger than expected.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expectedSize)
            throw new InvalidOperationException("The downloaded update package is incomplete.");
    }

    private static void ExtractArchive(ZipArchive archive, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update archive contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private static void ValidatePayload(string path, Version expectedVersion, string component)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"The update archive does not contain the {component} executable.");
        var information = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(information.ProductName, ProductName, StringComparison.Ordinal)
            || information.ProductVersion is null
            || ParseVersion(information.ProductVersion) != expectedVersion)
            throw new InvalidOperationException($"The update {component} identity or version is invalid.");
    }

    private static void CleanOldUpdates(string updateRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(updateRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var file in Directory.EnumerateFiles(updateRoot, "*.partial"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("target_commitish")] string? TargetCommitish,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
