namespace KiloviewPcOnboarding;

internal static class ConfigurationTransaction
{
    internal static async Task<T> RunAsync<T>(Func<Task<T>> apply, Func<CancellationToken, Task> restore)
    {
        try { return await apply(); }
        catch (Exception failure)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await restore(recovery.Token); }
            catch (Exception rollback)
            {
                throw new AggregateException("Onboarding failed and automatic recovery was incomplete. Local network/NDI repair is required. " + failure.Message + " Recovery: " + rollback.Message, failure, rollback);
            }
            throw new InvalidOperationException("Onboarding failed; the previous network and NDI configuration was restored. " + failure.Message, failure);
        }
    }

    internal static Dictionary<string, byte[]?> CaptureFiles(IEnumerable<string> paths) => paths.Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.OrdinalIgnoreCase);

    internal static void RestoreFiles(IReadOnlyDictionary<string, byte[]?> files)
    {
        var failures = new List<Exception>();
        foreach (var (path, bytes) in files)
        {
            try
            {
                if (bytes is null) { if (File.Exists(path)) File.Delete(path); continue; }
                // A readable file locked against replacement may already contain the snapshot.
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".recovery-" + Guid.NewGuid().ToString("N");
                try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failures.Add(new IOException($"Could not restore configuration file '{path}'.", ex)); }
        }
        if (failures.Count > 0) throw new AggregateException("Configuration file recovery was incomplete.", failures);
    }
}
