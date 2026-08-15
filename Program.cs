using System.Reflection;

namespace KiloviewPcOnboarding;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var icon = LoadIcon();
        RemoteOnboardingOptions? remote;
        try
        {
            remote = RemoteOptions(args);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            MessageBox.Show(
                ex.Message,
                "NDI Configurator PC Agent Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }
        if (remote is not null)
        {
            if (!ConsentStore.IsAccepted("1.0"))
            {
                MessageBox.Show(
                    "The NDI Configurator PC Agent EULA has not been accepted on this PC. Reinstall the agent locally first.",
                    "NDI Configurator PC Agent Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            Application.Run(new RemoteOnboardingApplicationContext(remote));
            return;
        }
        if (AgentInstallationService.IsConfigured())
        {
            var network = AgentInstallationService.PreferredNetwork();
            var update = network is null
                ? new AgentInstallationResult(
                    false,
                    false,
                    "The installed NDI Configurator PC Agent network selection could not be read.")
                : AgentInstallationService.InstallOrUpdate(network);
            MessageBox.Show(
                update.Installed
                    ? "NDI Configurator PC Agent is installed and up to date. Onboarding must be started remotely from NDI Job Configurator."
                    : $"NDI Configurator PC Agent could not be updated.\n\n{update.Message}",
                "NDI Configurator PC Agent Setup",
                MessageBoxButtons.OK,
                update.Installed ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            return;
        }
        if (!ConsentStore.IsAccepted("1.0"))
        {
            using var agreement = new EulaForm(icon);
            if (agreement.ShowDialog() != DialogResult.OK) return;
            ConsentStore.Record("1.0");
        }
        Application.Run(new MainForm(icon));
    }

    internal static RemoteOnboardingOptions? RemoteOptions(string[] args)
    {
        if (!args.Any(value => string.Equals(
                value,
                "--remote-onboarding",
                StringComparison.OrdinalIgnoreCase)))
            return null;
        var configurator = Argument(args, "--configurator")
            ?? throw new ArgumentException("The remote Configurator URL is missing.");
        var endpointId = Argument(args, "--endpoint-id")
            ?? throw new ArgumentException("The NDI Configurator PC Agent endpoint identity is missing.");
        var requestingAddress = Argument(args, "--requesting-address")
            ?? throw new ArgumentException("The requesting Configurator address is missing.");
        if (!Uri.TryCreate(configurator, UriKind.Absolute, out var baseUri))
            throw new UriFormatException("The remote Configurator URL is invalid.");
        return new(baseUri, endpointId, requestingAddress);
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.FindIndex(args, value =>
            string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KiloviewPcOnboarding.BrandIcon.ico")
            ?? throw new InvalidOperationException("The application icon is missing.");
        return new Icon(stream);
    }
}
