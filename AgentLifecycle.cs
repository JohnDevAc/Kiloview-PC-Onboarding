using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;

namespace KiloviewPcOnboarding;

internal static class AgentLifecycle
{
    internal static int Run(string path, string[] args)
    {
        LifecycleOperation operation;
        try { operation = new LifecycleOperation(path, InteractiveProfile.Sid); }
        catch { return 1; } // No safe, correlated output location is known yet.
        return Execute(operation, op =>
        {
            var expectedHash = Argument(args, "--lifecycle-sha256");
            if (expectedHash is not null && expectedHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(op.RequestPath))))
                throw new InvalidDataException("Lifecycle intent changed during elevation.");
            if (op.Request.Action == "inspect")
            {
                var evidence = AgentInstallationService.InspectLifecycle();
                var busy = SetupOperationLease.IsBusy();
                op.Write("succeeded", "PC Agent installation inspected; no configuration changed.", "inspect",
                    components: [evidence], recoveryRequired: evidence.RecoveryRequired,
                    safeToRetry: busy is null ? null : !busy.Value && !evidence.RecoveryRequired);
                return 0;
            }
            if (!op.Request.PreserveData)
            {
                op.Write("blocked", "Lifecycle removal preserves Agent state, endpoint identity and shared NDI settings. Purge is not supported.", errorCode: "preserve-data-required");
                return 1;
            }
            if (!IsAdministrator())
            {
                op.Write("running", "Waiting for Windows administrator approval.", "waiting-input", safeToCancel: true);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--lifecycle-request"); start.ArgumentList.Add(op.RequestPath);
                start.ArgumentList.Add("--lifecycle-sha256");
                start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(op.RequestPath))));
                using var worker = Process.Start(start) ?? throw new IOException("Windows did not start Agent maintenance.");
                worker.WaitForExit(); // Interactive UAC/licence is deliberately not killed on a timer.
                op.VerifyWorkerExit(worker.ExitCode);
                return worker.ExitCode;
            }
            using var lease = SetupOperationLease.Acquire(timeout: TimeSpan.Zero);
            if (op.Request.Action == "uninstall")
            {
                op.Write("running", "Removing PC Agent software; saved state and shared NDI remain.", "uninstall", safeToCancel: false);
                var effects = AgentInstallationService.UninstallLifecycle();
                op.Write("succeeded", "PC Agent removed. Saved state, endpoint identity, NDI Tools and NDI settings were retained.", "complete",
                    components: [AgentInstallationService.InspectLifecycle()], partialEffects: effects);
                return 0;
            }
            var packageVersion = PackageInstallation.VersionOf(Environment.ProcessPath!)!;
            PackageInstallation.ValidateRequestedVersion(op.Request.RequestedVersion, packageVersion);
            if (!ConsentStore.IsAccepted("1.0"))
            {
                op.Write("running", "Waiting for acceptance of the PC Agent licence.", "waiting-input", safeToCancel: true);
                ApplicationConfiguration.Initialize();
                using var agreement = new EulaForm(Program.LoadIcon());
                if (agreement.ShowDialog() != DialogResult.OK) throw new OperationCanceledException("PC Agent licence was declined.");
                ConsentStore.Record("1.0");
            }
            return InstallPackage(op, AgentInstallationService.UpgradePackage, AgentInstallationService.InspectLifecycle);
        }, () => AgentInstallationService.InspectLifecycle());
    }

    internal static int InstallPackage(LifecycleOperation op, Func<string?, AgentInstallationResult> upgrade,
        Func<AgentLifecycleEvidence> inspect)
    {
        op.Write("running", "Validating the complete package and saved configuration.", "preflight", safeToCancel: false);
        var result = upgrade(op.Request.RequestedVersion);
        if (!result.Installed)
        {
            var remaining = inspect();
            op.Write("partial", result.Message, "installation", "installation-incomplete", [remaining],
                remaining.RecoveryRequired, ["Package or integration work may have completed; inspect before retrying."]);
            return 1;
        }
        var after = inspect();
        if (after.Installed != true) throw new IOException("The installed Agent/Setup pair did not verify.");
        PackageInstallation.ValidateRequestedVersion(op.Request.RequestedVersion, after.Version);
        PackageInstallation.ValidateRequestedVersion(op.Request.RequestedVersion, after.SetupVersion);
        // Equal versions can still repair files, registration or startup.
        op.Write("succeeded", result.Message, "complete", components: [after], recoveryRequired: after.RecoveryRequired);
        return 0;
    }

    // Shared with harmless producer fixtures. Results are durable even when the
    // work callback fails; an absent result is never translated into success.
    internal static int Execute(LifecycleOperation operation, Func<LifecycleOperation, int> work, Func<AgentLifecycleEvidence>? inspect = null)
    {
        try { return work(operation); }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException || ex is Win32Exception { NativeErrorCode: 1223 };
            var busy = ex is LifecycleBusyException;
            AgentLifecycleEvidence? observation = null;
            try { observation = inspect?.Invoke(); } catch { }
            object[]? evidence = observation is null ? null : [observation];
            // Inspection here is intentionally supplied only by the real work
            // path; generic fixture errors must not access the machine.
            var partial = ex is LifecyclePartialException || observation?.RecoveryRequired == true;
            operation.Write(cancelled ? "cancelled" : busy ? "blocked" : partial ? "partial" : "failed", ex.Message,
                errorCode: cancelled ? "cancelled" : busy ? "operation-active" : partial ? "partial-removal" : "operation-failed",
                components: evidence, recoveryRequired: observation?.RecoveryRequired ?? partial,
                partialEffects: (ex as LifecyclePartialException)?.Effects,
                nextAction: observation?.RecoveryRequired == true ? "Run repair from the complete Agent package to recover the interrupted operation." : null);
            return cancelled ? 1602 : busy ? 1618 : 1;
        }
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static int Elevate(string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Setup could not be started.");
            child.WaitForExit(); return child.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return 1602; }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class LifecyclePartialException(string message, string[] effects) : IOException(message)
{
    internal string[] Effects { get; } = effects;
}
