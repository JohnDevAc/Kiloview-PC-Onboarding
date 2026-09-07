using System.Reflection;
using NdiSuite.Onboarding;

namespace KiloviewPcOnboarding;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var recovery = args.FirstOrDefault() == "--recover-installed-package";
            if (recovery) DeferredPackageRecovery.WaitForParent(args);
            using var operation = SetupOperationLease.Acquire();
            if (recovery) return AgentInstallationService.RecoverInstalledPackage();
            return Run(args);
        }
        catch (Exception ex)
        {
            if (args.Contains("--server-command", StringComparer.Ordinal))
            {
                var trace = new OnboardingTrace();
                trace.Step("setup-startup", "Acquiring the Setup operation lock and starting the installed utility.");
                using var output = Console.OpenStandardOutput();
                System.Text.Json.JsonSerializer.Serialize(output, new ServerOnboardingResponse(
                    1, false, NdiToolsService.UtilityVersion(), Error: ex.Message,
                    FailureReport: trace.Failure(Guid.Empty.ToString("D"), Guid.NewGuid().ToString("D"), NdiToolsService.UtilityVersion(), ex)),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            }
            else
            {
                try
                {
                    if (RemoteOptions(args) is { } remote)
                    {
                        var trace = new OnboardingTrace();
                        trace.Step("setup-startup", "Starting Setup and checking the installed licence.");
                        var network = AgentInstallationService.PreferredNetwork();
                        OnboardingDiagnostics.CaptureRemoteAsync(AgentInstallationService.ConfigurationPath, remote.EndpointId,
                            remote.AttemptId, remote.RequestingAddress, network?.Id ?? AgentInstallationService.ConfiguredAdapterId ?? "unknown",
                            network?.Address, NdiToolsService.UtilityVersion(), trace, ex).GetAwaiter().GetResult();
                    }
                }
                catch { /* Preserve the original startup error. Agent also records a failed Setup exit. */ }
                MessageBox.Show(ex.Message, "NDI Configurator PC Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Contains("--server-command", StringComparer.Ordinal))
            return ServerOnboardingCommand.RunAsync().GetAwaiter().GetResult();
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
            return 1;
        }
        if (remote is not null)
        {
            if (!ConsentStore.IsAccepted("1.0"))
                throw new InvalidOperationException("The NDI Configurator PC Agent EULA has not been accepted on this PC. Reinstall the agent locally first.");
            using var context = new RemoteOnboardingApplicationContext(remote);
            Application.Run(context);
            return context.ExitCode;
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
            return update.Installed ? 0 : 1;
        }
        if (!ConsentStore.IsAccepted("1.0"))
        {
            using var agreement = new EulaForm(icon);
            if (agreement.ShowDialog() != DialogResult.OK) return 2;
            ConsentStore.Record("1.0");
        }
        Application.Run(new MainForm(icon));
        return 0;
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
        return new(baseUri, endpointId, requestingAddress, Argument(args, "--attempt-id"));
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
