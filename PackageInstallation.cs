using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Security.Cryptography;

namespace KiloviewPcOnboarding;

internal static class PackageInstallation
{
    internal static IDisposable AcquireInstallationLock(TimeSpan? timeout = null, string? name = null)
    {
        var mutex = new Mutex(false, name ?? @"Global\NDIConfiguratorPcAgentInstall");
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another PC Agent installation is running. Wait for it to finish and retry.");
            return new InstallationLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class InstallationLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    internal static int Compare(string left, string right)
    {
        static (Version Core, string[]? Pre) Parse(string text)
        {
            var parts = text.TrimStart('v').Split('+')[0].Split('-', 2);
            var v = Version.Parse(parts[0]);
            return (new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)),
                parts.Length == 1 ? null : parts[1].Split('.'));
        }
        var a = Parse(left); var b = Parse(right);
        var order = a.Core.CompareTo(b.Core);
        if (order != 0) return order;
        if (a.Pre is null) return b.Pre is null ? 0 : 1;
        if (b.Pre is null) return -1;
        for (var i = 0; i < Math.Max(a.Pre.Length, b.Pre.Length); i++)
        {
            if (i >= a.Pre.Length) return -1;
            if (i >= b.Pre.Length) return 1;
            var an = BigInteger.TryParse(a.Pre[i], out var av);
            var bn = BigInteger.TryParse(b.Pre[i], out var bv);
            order = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : StringComparer.Ordinal.Compare(a.Pre[i], b.Pre[i]);
            if (order != 0) return order;
        }
        return 0;
    }

    internal static bool Retain(string? agent, string? setup, string package)
    {
        if (!(agent is not null && Compare(agent, package) > 0 || setup is not null && Compare(setup, package) > 0))
            return false;
        if (agent is null || setup is null || Compare(agent, setup) != 0)
            throw new InvalidOperationException("A newer PC Agent installation has mixed or missing components. Repair it with a matching or newer complete package.");
        return true;
    }

    internal static string? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != "NDI Configurator PC Agent" || info.ProductVersion is null)
            throw new InvalidOperationException("The PC Agent component has invalid product/version metadata: " + Path.GetFileName(path));
        _ = Compare(info.ProductVersion, info.ProductVersion);
        return info.ProductVersion;
    }

    // Stage every file before stopping the running Agent; preserve rollback material
    // until the entire pair is replaced. The helper is also exercised with fixture files.
    internal static void Replace(IReadOnlyDictionary<string, string> sources, Action beforeReplace,
        Action<string, string>? move = null)
    {
        if (sources.Count == 0) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(sources.Keys.First()))!;
        if (sources.Keys.Any(path => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A package replacement must stay within one installation directory.");
        Recover(directory);
        var recovery = Path.Combine(directory, ".pc-agent-install-recovery");
        var staged = new Dictionary<string, string>();
        var originals = sources.Keys.ToDictionary(path => Path.GetFileName(path), File.Exists);
        move ??= (source, target) => File.Move(source, target, true);
        Directory.CreateDirectory(recovery);
        try
        {
            foreach (var (name, existed) in originals)
                if (existed) CopyDurably(Path.Combine(directory, name), Path.Combine(recovery, name));
            // Publish the journal only after every original is safely copied.
            var entries = originals.ToDictionary(item => item.Key, item => new BackupEntry(item.Value,
                item.Value ? Hash(Path.Combine(recovery, item.Key)) : null));
            WriteJournal(recovery, entries);
            foreach (var (destination, source) in sources)
            {
                var temporary = destination + ".staged-" + Guid.NewGuid().ToString("N");
                staged[destination] = temporary;
                CopyDurably(source, temporary);
            }
            beforeReplace();
            try { foreach (var (destination, temporary) in staged) move(temporary, destination); }
            catch (Exception failure)
            {
                try { Recover(directory); }
                catch (Exception rollback) { throw new AggregateException("PC Agent replacement failed; restore the matching complete package locally.", failure, rollback); }
                throw new IOException("PC Agent replacement failed; the previous complete pair was restored.", failure);
            }
            File.Delete(Path.Combine(recovery, "manifest.json"));
        }
        finally
        {
            foreach (var temporary in staged.Values) if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(recovery) && !File.Exists(Path.Combine(recovery, "manifest.json")))
                Directory.Delete(recovery, true);
        }
    }

    private static void CopyDurably(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    internal static void Recover(string directory, string? runningSetupPath = null)
    {
        directory = Path.GetFullPath(directory);
        var recovery = Path.Combine(directory, ".pc-agent-install-recovery");
        var journal = Path.Combine(recovery, "manifest.json");
        if (!File.Exists(journal))
        {
            // No published journal means replacement never started.
            if (Directory.Exists(recovery)) Directory.Delete(recovery, true);
            return;
        }
        var originals = ReadJournal(journal);
        foreach (var name in originals.Keys)
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new IOException("The PC Agent recovery journal contains an invalid filename.");
        foreach (var (name, entry) in originals)
        {
            var destination = Path.Combine(directory, name);
            if (entry.Existed)
            {
                var backup = Path.Combine(recovery, name);
                if (entry.Sha256 is not null && !string.Equals(Hash(backup), entry.Sha256, StringComparison.Ordinal))
                    throw new IOException("The PC Agent recovery backup is corrupt: " + name);
                // The failed move may never have changed this file. In particular,
                // do not overwrite an unchanged Setup executable that is still open.
                if (FilesEqual(backup, destination)) continue;
                if (string.Equals(Path.GetFullPath(destination), runningSetupPath ?? Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    throw new RunningSetupRecoveryException();
                var temporary = destination + ".restore-" + Guid.NewGuid().ToString("N");
                CopyDurably(backup, temporary);
                try { File.Move(temporary, destination, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else if (File.Exists(destination)) File.Delete(destination);
        }
        File.Delete(journal);
        Directory.Delete(recovery, true);
    }

    internal sealed class RunningSetupRecoveryException() : IOException(
        "The running Setup executable needs recovery from an external helper.");
    private sealed record BackupEntry(bool Existed, string? Sha256);
    private static Dictionary<string, BackupEntry> ReadJournal(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.EnumerateObject().ToDictionary(item => item.Name, item =>
            item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new BackupEntry(item.Value.GetBoolean(), null) // Previous journal format.
                : JsonSerializer.Deserialize<BackupEntry>(item.Value.GetRawText())
                    ?? throw new IOException("The PC Agent recovery journal is invalid."));
    }

    private static void WriteJournal(string directory, Dictionary<string, BackupEntry> entries)
    {
        var temporary = Path.Combine(directory, "manifest.tmp");
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, entries);
            output.Flush(true);
        }
        File.Move(temporary, Path.Combine(directory, "manifest.json"), true);
    }

    internal static bool FilesEqual(string first, string second) => File.Exists(first) && File.Exists(second)
        && new FileInfo(first).Length == new FileInfo(second).Length && Hash(first) == Hash(second);
    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
