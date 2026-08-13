using System.Reflection;
using System.Diagnostics;

namespace KiloviewPcAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var installedAgent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Kiloview",
            "PC Agent",
            "Kiloview PC Agent.exe");
        var runningAgent = Environment.ProcessPath;
        if (File.Exists(installedAgent)
            && runningAgent is not null
            && !Path.GetFullPath(runningAgent).Equals(
                Path.GetFullPath(installedAgent),
                StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(installedAgent) { UseShellExecute = true });
            return;
        }

        using var showStatusRequest = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            "Local\\KiloviewPcAgentShowStatus");
        using var singleInstance = new Mutex(true, "Local\\KiloviewPcAgent", out var created);
        if (!created)
        {
            showStatusRequest.Set();
            return;
        }
        if (AgentStore.Read() is null)
            return;

        ApplicationConfiguration.Initialize();
        using var icon = LoadIcon();
        Application.Run(new AgentApplicationContext(icon, showStatusRequest));
    }

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KiloviewPcAgent.BrandIcon.ico")
            ?? throw new InvalidOperationException("The application icon is missing.");
        return new Icon(stream);
    }
}
