using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace KiloviewPcOnboarding;

internal sealed record LifecycleRequest(int SchemaVersion, string OperationId, string ProductId, string Action,
    string ResultPath, bool PreserveData = true, string? RequestedVersion = null);
internal sealed record LifecycleResult(int SchemaVersion, string OperationId, string ProductId, string Action,
    string Outcome, DateTimeOffset UpdatedUtc, string Message, string? Stage = null, string? ErrorCode = null,
    string? NextAction = null, bool? SafeToCancel = null, bool? RecoveryRequired = null,
    object[]? Components = null, string[]? PartialEffects = null, bool? SafeToRetry = null, string? ActiveOperationId = null);

// File protocol only. The callback used by the executable and isolated producer
// fixtures is the same; neither request fields nor result fields are commands.
internal sealed class LifecycleOperation
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal LifecycleRequest Request { get; }
    internal string RequestPath { get; }
    private readonly string ownerSid;

    internal LifecycleOperation(string path, string? expectedOwner = null)
    {
        ownerSid = expectedOwner ?? WindowsIdentity.GetCurrent().User!.Value;
        RequestPath = SafePath(path);
        ValidateOwnedDirectory(Path.GetDirectoryName(RequestPath)!, ownerSid);
        if (new FileInfo(RequestPath).Length > 16 * 1024) throw new InvalidDataException("Lifecycle request exceeds 16 KiB.");
        Request = JsonSerializer.Deserialize<LifecycleRequest>(File.ReadAllText(RequestPath), Json)
            ?? throw new InvalidDataException("The lifecycle request is empty.");
        if (Request.SchemaVersion != 1 || Request.ProductId != "pc-agent"
            || !Guid.TryParseExact(Request.OperationId, "D", out var id) || id == Guid.Empty
            || Request.Action is not ("inspect" or "install" or "update" or "repair" or "uninstall"))
            throw new InvalidDataException("Unsupported lifecycle schema, identity, product or action.");
        var result = SafePath(Request.ResultPath);
        if (!string.Equals(Path.GetDirectoryName(result), Path.GetDirectoryName(RequestPath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(result, RequestPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(result), "result.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The lifecycle result must be result.json in the request's owned operation directory.");
        if (File.Exists(result))
        {
            var previous = JsonSerializer.Deserialize<LifecycleResult>(File.ReadAllText(result), Json);
            if (previous is null || previous.SchemaVersion != 1 || previous.OperationId != Request.OperationId
                || previous.ProductId != Request.ProductId || previous.Action != Request.Action)
                throw new InvalidDataException("A mismatched lifecycle result already exists.");
        }
    }

    internal void Write(string outcome, string message, string? stage = null, string? errorCode = null,
        object[]? components = null, bool? recoveryRequired = null, string[]? partialEffects = null, bool? safeToCancel = null,
        bool? safeToRetry = null, string? nextAction = null)
    {
        ValidateOwnedDirectory(Path.GetDirectoryName(RequestPath)!, ownerSid);
        SafePath(Request.ResultPath);
        var value = new LifecycleResult(1, Request.OperationId, Request.ProductId, Request.Action,
            outcome, DateTimeOffset.UtcNow, message, stage, errorCode, SafeToCancel: safeToCancel,
            RecoveryRequired: recoveryRequired, Components: components, PartialEffects: partialEffects,
            SafeToRetry: safeToRetry, NextAction: nextAction);
        WriteAtomic(Request.ResultPath, value);
    }

    internal static void WriteAtomic<T>(string path, T value)
    {
        SafePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void VerifyWorkerExit(int exitCode)
    {
        SafePath(Request.ResultPath);
        var result = JsonSerializer.Deserialize<LifecycleResult>(File.ReadAllText(Request.ResultPath), Json);
        if (result is null || result.SchemaVersion != 1 || result.OperationId != Request.OperationId
            || result.ProductId != Request.ProductId || result.Action != Request.Action || result.Outcome == "running")
            throw new IOException("The elevated maintenance worker exited without a final correlated result. Inspect the installation before retrying.");
        if (exitCode != 0 && result.Outcome is "succeeded" or "no-change")
            throw new IOException("The maintenance worker failed after reporting completion. Inspect the actual installation before retrying.");
    }

    internal static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path[Path.GetPathRoot(path)!.Length..].Contains(':'))
            throw new InvalidDataException("Lifecycle paths must be absolute local paths without alternate streams.");
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 && !NdiLifecycle.CloudPath.IsAllowed(current))
                throw new IOException("Lifecycle paths cannot traverse filesystem links.");
        return full;
    }

    internal static void ValidateOwnedDirectory(string path, string owner)
    {
        SafePath(path);
        var security = new DirectoryInfo(path).GetAccessControl();
        var actualOwner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        var allowed = new[] { owner, "S-1-5-18", "S-1-5-32-544" };
        if (!allowed.Contains(actualOwner)) throw new UnauthorizedAccessException("The lifecycle operation directory has another owner.");
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0
                && !allowed.Contains(rule.IdentityReference.Value) && rule.IdentityReference.Value != "S-1-3-0")
                throw new UnauthorizedAccessException("The lifecycle operation directory permits writes by another account.");
    }

    internal static void ValidateTree(string directory)
    {
        SafePath(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            SafePath(entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }

    internal static int ExitCode(string outcome) => outcome switch
    {
        "succeeded" or "no-change" => 0, "cancelled" => 1602, "restart-required" => 3010, "busy" => 1618, _ => 1
    };
}
