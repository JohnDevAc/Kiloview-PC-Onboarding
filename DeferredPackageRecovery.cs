using System.Diagnostics;

namespace KiloviewPcOnboarding;

internal static class DeferredPackageRecovery
{
    internal static void Queue(string installationDirectory)
    {
        // Inherit the protected Program Files ACL, rather than executing an elevated
        // repair helper from a user-writable download or temporary directory.
        var helperDirectory = Path.Combine(installationDirectory, ".recovery-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        var executable = Environment.ProcessPath ?? throw new IOException("Setup executable is unavailable.");
        foreach (var source in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!, Path.GetFileNameWithoutExtension(executable) + ".*"))
            File.Copy(source, Path.Combine(helperDirectory, Path.GetFileName(source)), false);
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(helperDirectory, Path.GetFileName(executable)))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = helperDirectory
        };
        start.ArgumentList.Add("--recover-installed-package");
        start.ArgumentList.Add(parent.Id.ToString());
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
        using var helper = Process.Start(start) ?? throw new IOException("The package recovery helper could not start.");
    }

    internal static void WaitForParent(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var id) || !long.TryParse(args[2], out var ticks))
            throw new ArgumentException("Invalid package recovery request.");
        try
        {
            using var parent = Process.GetProcessById(id);
            if (parent.StartTime.ToUniversalTime().Ticks == ticks && !parent.WaitForExit(15 * 60 * 1000))
                throw new IOException("Close the original Setup before retrying package recovery.");
        }
        catch (ArgumentException) { /* Original process has already exited. */ }
    }
}
