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
        foreach (var (path, bytes) in files)
        {
            if (bytes is null) { if (File.Exists(path)) File.Delete(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".recovery-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
