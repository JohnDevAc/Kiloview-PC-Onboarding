using System.Reflection;

namespace KiloviewPcAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, "Local\\KiloviewPcAgent", out var created);
        if (!created)
            return;
        if (AgentStore.Read() is null)
            return;

        ApplicationConfiguration.Initialize();
        using var icon = LoadIcon();
        Application.Run(new AgentApplicationContext(icon));
    }

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KiloviewPcAgent.BrandIcon.ico")
            ?? throw new InvalidOperationException("The application icon is missing.");
        return new Icon(stream);
    }
}
