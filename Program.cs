using System.Reflection;

namespace KiloviewPcOnboarding;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var icon = LoadIcon();
        if (!ConsentStore.IsAccepted("1.0"))
        {
            using var agreement = new EulaForm(icon);
            if (agreement.ShowDialog() != DialogResult.OK) return;
            ConsentStore.Record("1.0");
        }
        Application.Run(new MainForm(icon));
    }

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KiloviewPcOnboarding.BrandIcon.ico")
            ?? throw new InvalidOperationException("The application icon is missing.");
        return new Icon(stream);
    }
}
