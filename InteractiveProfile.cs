using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KiloviewPcOnboarding;

// Resolve the real desktop user from a Windows process token, never a supplied path/SID.
internal static class InteractiveProfile
{
    private static readonly Lazy<string> DesktopSid = new(ResolveSid);
    internal static string Sid => DesktopSid.Value;
    internal static string LocalApplicationData
    {
        get
        {
            using var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + Sid);
            var root = profile?.GetValue("ProfileImagePath") as string
                ?? throw new InvalidOperationException("The interactive Windows profile could not be resolved.");
            return Path.Combine(Environment.ExpandEnvironmentVariables(root), "AppData", "Local");
        }
    }

    internal static RegistryKey OpenUserHive() => Registry.Users.OpenSubKey(Sid, writable: true)
        ?? throw new InvalidOperationException("The interactive user's registry profile is not loaded. Sign in before running Setup.");

    private static string ResolveSid()
    {
        var current = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var session = Process.GetCurrentProcess().SessionId;
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shell in Process.GetProcessesByName("explorer"))
        {
            using (shell)
            {
                if (shell.SessionId != session) continue;
                if (!OpenProcessToken(shell.Handle, 8, out var token))
                    throw new InvalidOperationException("The desktop user's identity could not be verified. Close Setup and retry from that user's desktop.");
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    if (identity.User is { } user) users.Add(user.Value);
            }
        }
        if (users.Count > 1) throw new InvalidOperationException("Multiple desktop owners were found in this session. Setup cannot select a profile safely.");
        return users.SingleOrDefault() ?? current;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
