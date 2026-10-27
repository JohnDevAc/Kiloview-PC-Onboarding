namespace KiloviewPcOnboarding;

internal sealed class LifecycleBusyException(string message, Exception? inner = null) : IOException(message, inner);

// File sharing is process-owned, so this lease remains valid across async/UI threads.
internal static class SetupOperationLease
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NDI Configurator PC Agent", "setup-operation.lock");
    internal static IDisposable? TryAcquireForReconciliation(string? path = null)
    {
        try { return new FileStream(path ?? DefaultPath, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static IDisposable Acquire(string? path = null, TimeSpan? timeout = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
            catch (IOException ex) { throw new LifecycleBusyException("Another PC Agent Setup or onboarding operation is active. Close it or wait for it to finish, then retry.", ex); }
        }
    }

    internal static bool? IsBusy(string? path = null)
    {
        try { using var lease = new FileStream(path ?? DefaultPath, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
