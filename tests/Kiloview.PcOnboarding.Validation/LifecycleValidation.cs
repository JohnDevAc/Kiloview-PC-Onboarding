using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KiloviewPcOnboarding;

internal static class LifecycleValidation
{
    internal static int Fixture(string request, string outcome)
    {
        var operation = new LifecycleOperation(request);
        return AgentLifecycle.Execute(operation, op =>
        {
            op.Write("running", "Isolated Agent protocol fixture; no machine operations.", "fixture", safeToCancel: true);
            if (outcome == "crash") return 17;
            if (outcome == "cancelled") throw new OperationCanceledException("Fixture cancellation.");
            if (outcome == "blocked") throw new LifecycleBusyException("Fixture lifecycle lock is held.");
            if (outcome == "partial") throw new LifecyclePartialException("Fixture partial removal.", ["Fixture effect only."]);
            if (outcome is not ("succeeded" or "restart-required")) throw new ArgumentException("Unsupported fixture outcome.");
            op.Write(outcome, "Isolated Agent fixture completed.", "fixture", components: [new AgentLifecycleEvidence(
                "pc-agent", true, false, false, null, "fixture", "Development", "fixture", true, false, null, false, null)]);
            return LifecycleOperation.ExitCode(outcome);
        });
    }

    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "lifecycle", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        SecureDirectory(directory);
        var path = Path.Combine(directory, "request.json");
        var request = new LifecycleRequest(1, Path.GetFileName(directory), "pc-agent", "inspect", Path.Combine(directory, "result.json"));
        LifecycleOperation.WriteAtomic(path, request);
        Check(Fixture(path, "succeeded") == 0, "Fixture success failed.");
        var result = JsonSerializer.Deserialize<LifecycleResult>(File.ReadAllText(request.ResultPath), LifecycleOperation.Json)!;
        Check(result.OperationId == request.OperationId && result.Outcome == "succeeded", "Correlated result was lost.");
        var liveOperation = new LifecycleOperation(path);
        liveOperation.VerifyWorkerExit(0);
        Reject(() => liveOperation.VerifyWorkerExit(1), "Contradictory worker failure was accepted.");
        liveOperation.Write("running", "Waiting for worker");
        Reject(() => liveOperation.VerifyWorkerExit(17), "Worker exit without final result was accepted.");
        Check(Fixture(path, "cancelled") == 1602 && Fixture(path, "blocked") == 1618
            && Fixture(path, "partial") == 1 && Fixture(path, "restart-required") == 3010, "Exit/outcome mapping failed.");
        LifecycleOperation.WriteAtomic(request.ResultPath, result with { OperationId = Guid.NewGuid().ToString("D") });
        Reject(() => _ = new LifecycleOperation(path), "Mismatched prior result accepted.");
        File.Delete(request.ResultPath);
        foreach (var invalid in new[] { request with { SchemaVersion = 2 }, request with { ProductId = "job" },
            request with { OperationId = Guid.Empty.ToString("D") }, request with { ResultPath = Path.Combine(root, "escape.json") } })
        {
            LifecycleOperation.WriteAtomic(path, invalid);
            Reject(() => _ = new LifecycleOperation(path), "Invalid request accepted.");
        }
        Console.WriteLine("LIFECYCLE_PROTOCOL_CORRELATION_AND_OUTCOMES=PASS");

        var saved = Path.Combine(directory, "state.json"); var legacy = Path.Combine(directory, "legacy.json");
        string? AgentVersion(string file) => file.EndsWith(" Setup.exe", StringComparison.Ordinal) ? "0.7.5-dev.1" : "0.7.5-dev.1";
        var evidence = AgentInstallationService.InspectLifecycle(directory, saved, legacy, AgentVersion, () => true, () => []);
        Check(evidence.Installed == true && evidence.PairMatches == true && evidence.Configured == false && evidence.Running == true && evidence.Healthy is null,
            "Installed, configured, running and health were conflated.");
        var adapter = Guid.NewGuid().ToString("D");
        var state = new { schemaVersion = 1, endpointId = Guid.NewGuid().ToString("D"), adapterId = adapter,
            adapterName = "Fixture", address = "192.0.2.15", prefixLength = 24, memberships = Array.Empty<object>() };
        File.WriteAllText(legacy, JsonSerializer.Serialize(state));
        evidence = AgentInstallationService.InspectLifecycle(directory, saved, legacy, AgentVersion, () => false, () => []);
        Check(evidence.Configured == true && evidence.Onboarded == false && evidence.NetworkAvailable == false, "Configured state required onboarding or current network.");
        File.WriteAllText(saved, "invalid current state");
        evidence = AgentInstallationService.InspectLifecycle(directory, saved, legacy, AgentVersion, () => false, () => []);
        Check(evidence.Configured == false && evidence.Error is not null, "Corrupt current state fell back to legacy.");
        evidence = AgentInstallationService.InspectLifecycle(directory, saved, legacy,
            file => file.EndsWith(" Setup.exe", StringComparison.Ordinal) ? "0.7.4" : "0.7.5", () => false, () => []);
        Check(evidence.Installed == false && evidence.PairMatches == false, "Mixed installed pair accepted.");
        evidence = AgentInstallationService.InspectLifecycle(directory, saved, legacy,
            _ => throw new UnauthorizedAccessException("Fixture inaccessible metadata"), () => null, () => []);
        Check(evidence.Installed is null && evidence.Running is null && evidence.PairMatches is null, "Unknown installation evidence was converted to false.");
        Console.WriteLine("LIFECYCLE_READINESS_PRECEDENCE_AND_MATCHING_PAIR=PASS");

        var removed = Path.Combine(directory, "remove.exe"); var locked = Path.Combine(directory, "locked.exe");
        File.WriteAllText(removed, "fixture"); File.WriteAllText(locked, "fixture");
        var registrations = 0;
        using (var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { AgentInstallationService.RemoveOwnedFiles([removed, locked], () => { }, () => { }, () => registrations++); throw new Exception("Locked file removal claimed success."); }
            catch (LifecyclePartialException) { }
        }
        Check(!File.Exists(removed) && File.Exists(locked) && registrations == 0, "Partial uninstall lost its retry registration.");
        AgentInstallationService.RemoveOwnedFiles([removed, locked], () => { }, () => { }, () => registrations++);
        Check(!File.Exists(locked) && registrations == 1, "Partial removal retry failed.");
        Console.WriteLine("LIFECYCLE_PARTIAL_REMOVAL_RETRY=PASS");
        Check(NdiLifecycle.CloudPath.IsCloudTag(0x9000601a) && NdiLifecycle.CloudPath.IsCloudTag(0x9000e01a)
            && !NdiLifecycle.CloudPath.IsCloudTag(0xa0000003) && !NdiLifecycle.CloudPath.IsCloudTag(0xa000000c), "Cloud tag safety failed.");
        using (SetupOperationLease.Acquire(Path.Combine(directory, "fixture.lock"), TimeSpan.Zero))
            Reject(() => SetupOperationLease.Acquire(Path.Combine(directory, "fixture.lock"), TimeSpan.Zero).Dispose(), "Concurrent operation acquired a held lease.");
        Console.WriteLine("LIFECYCLE_LOCK_AND_CLOUD_TAG_SAFETY=PASS");
    }

    internal static void SecureDirectory(string path)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var owner = WindowsIdentity.GetCurrent().User!; security.SetOwner(owner);
        foreach (var sid in new[] { owner.Value, "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return; }
        throw new Exception(message);
    }
}
