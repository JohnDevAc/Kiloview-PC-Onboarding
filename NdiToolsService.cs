using System.Diagnostics;
using Microsoft.Win32;
using System.Net.Http.Headers;
using System.Security;
using System.Text.RegularExpressions;

namespace KiloviewPcOnboarding;

internal sealed partial class NdiToolsService
{
    private static readonly Uri ToolsPage = new("https://ndi.video/tools/?download=windows");
    private static readonly Uri WindowsInstaller = new("https://downloads.ndi.tv/Tools/NDI%206%20Tools.exe");

    public async Task<NdiToolsStatus> CheckAsync(CancellationToken ct)
    {
        var installed = FindInstalledTools();
        var accessManager = installed?.AccessManagerPath;
        var installedVersion = installed?.Version;
        Version? currentVersion = null;
        string? onlineError = null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NDI-Configurator-PC-Agent", UtilityVersion()));
            var html = await client.GetStringAsync(ToolsPage, ct);
            var match = VersionPattern().Match(html);
            if (match.Success) Version.TryParse(match.Groups[1].Value, out currentVersion);
            if (currentVersion is null) onlineError = "The official NDI page did not report a release version.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            onlineError = $"The current NDI release could not be checked: {ex.Message}";
        }

        var message = installed is null
            ? "NDI Tools is not installed."
            : currentVersion is not null && installedVersion is not null && installedVersion < currentVersion
                ? $"NDI Tools {installedVersion} is installed; {currentVersion} is current."
                : $"NDI Tools {installedVersion?.ToString() ?? "version unknown"} is installed."
                    + (onlineError is null ? " It is current." : $" {onlineError}");
        return new(installed is not null, installedVersion, currentVersion, accessManager, message);
    }

    public async Task DownloadAndInstallAsync(
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"NDI-Tools-{Guid.NewGuid():N}.exe");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NDI-Configurator-PC-Agent", UtilityVersion()));
            using var response = await client.GetAsync(WindowsInstaller, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.RequestMessage?.RequestUri?.Host, "downloads.ndi.tv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The NDI installer download was redirected away from the official NDI download host.");
            var length = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (length is > 0) progress?.Report((int)Math.Min(100, total * 100 / length.Value));
                }
            }
            VerifyOfficialSignature(path);

            using var installer = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("Windows could not start the NDI Tools installer.");
            await installer.WaitForExitAsync(ct);
            if (installer.ExitCode != 0)
                throw new InvalidOperationException($"The NDI Tools installer exited with code {installer.ExitCode}.");
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
    }

    public static string UtilityVersion() =>
        typeof(NdiToolsService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static void VerifyOfficialSignature(string path)
    {
        var escaped = path.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$s=Get-AuthenticodeSignature -LiteralPath '{escaped}';"
            + "if($s.Status -ne 'Valid'){Write-Error ('Invalid signature: '+$s.Status);exit 2};"
            + "if($s.SignerCertificate.Subject -notmatch '(?i)(Vizrt|NDI|NewTek)'){Write-Error ('Unexpected publisher: '+$s.SignerCertificate.Subject);exit 3};"
            + "Write-Output $s.SignerCertificate.Subject";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows could not verify the NDI installer signature.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The downloaded NDI installer was not accepted as an official signed package. {error}{output}".Trim());
    }

    private static InstalledTools? FindInstalledTools()
    {
        var registered = RegisteredInstallations()
            .OrderByDescending(installation => installation.Version)
            .FirstOrDefault();
        if (registered is not null) return registered;

        var accessManager = AccessManagerCandidates().FirstOrDefault(File.Exists);
        return accessManager is null
            ? null
            : new InstalledTools(accessManager, FileVersion(accessManager));
    }

    private static Version? FileVersion(string path)
    {
        var value = FileVersionInfo.GetVersionInfo(path).ProductVersion
            ?? FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = NumericVersionPattern().Match(value);
        return match.Success && Version.TryParse(match.Value, out var parsed) ? parsed : null;
    }

    private static IEnumerable<string> AccessManagerCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(
            programFiles,
            "NDI",
            "NDI 6 Tools",
            "Access Manager",
            "Application.NdiGroupEditor.exe");
        yield return Path.Combine(programFiles, "NDI", "NDI 6 Tools", "Access Manager.exe");
        yield return Path.Combine(programFiles, "NDI", "NDI Tools", "Access Manager.exe");
        yield return Path.Combine(programFiles, "NewTek", "NDI 5 Tools", "Access Manager.exe");
    }

    private static IReadOnlyList<InstalledTools> RegisteredInstallations()
    {
        var installations = new List<InstalledTools>();
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        };
        foreach (var (hive, view) in locations)
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstall = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstall = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subkeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subkeyName);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    var publisher = entry?.GetValue("Publisher") as string;
                    if (displayName is null
                        || !ToolsNamePattern().IsMatch(displayName)
                        || publisher is null
                        || !PublisherPattern().IsMatch(publisher))
                        continue;

                    var installLocation = entry?.GetValue("InstallLocation") as string;
                    var accessManager = AccessManagerFrom(installLocation)
                        ?? AccessManagerCandidates().FirstOrDefault(File.Exists);
                    var version = ParseVersion(entry?.GetValue("DisplayVersion") as string)
                        ?? (accessManager is null ? null : FileVersion(accessManager));
                    installations.Add(new InstalledTools(accessManager, version));
                }
            }
            catch (Exception ex) when (ex is SecurityException
                or UnauthorizedAccessException
                or IOException)
            {
                // File-based detection below remains available when registry access is restricted.
            }
            finally
            {
                uninstall?.Dispose();
                baseKey?.Dispose();
            }
        }
        return installations;
    }

    private static string? AccessManagerFrom(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;
        var candidates = new[]
        {
            Path.Combine(installLocation, "Access Manager", "Application.NdiGroupEditor.exe"),
            Path.Combine(installLocation, "Access Manager.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = NumericVersionPattern().Match(value);
        return match.Success && Version.TryParse(match.Value, out var parsed) ? parsed : null;
    }

    private sealed record InstalledTools(string? AccessManagerPath, Version? Version);

    [GeneratedRegex(@"Version\s+([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+){1,3}")]
    private static partial Regex NumericVersionPattern();

    [GeneratedRegex(@"^NDI(?:\s+\d+)?\s+Tools$", RegexOptions.IgnoreCase)]
    private static partial Regex ToolsNamePattern();

    [GeneratedRegex(@"^(?:NDI|NewTek|Vizrt)", RegexOptions.IgnoreCase)]
    private static partial Regex PublisherPattern();
}
