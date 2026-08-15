namespace KiloviewPcOnboarding;

internal sealed class RemoteOnboardingApplicationContext : ApplicationContext
{
    private readonly RemoteOnboardingOptions _options;
    private bool _started;

    public RemoteOnboardingApplicationContext(RemoteOnboardingOptions options)
    {
        _options = options;
        Application.Idle += Start;
    }

    private async void Start(object? sender, EventArgs args)
    {
        if (_started)
            return;
        _started = true;
        Application.Idle -= Start;
        try
        {
            var result = await RemoteOnboardingService.ExecuteAsync(
                _options,
                CancellationToken.None);
            var network = result.NetworkChanged
                ? $"Network settings applied: {result.Address}/{result.PrefixLength}."
                : $"Network retained: {result.Address}/{result.PrefixLength}.";
            var ndi = result.NdiUpdateRequired
                ? $"\n\nNDI ACTION REQUIRED\n{result.NdiStatusMessage}\n"
                    + "Install the latest NDI Tools from https://ndi.video/tools/."
                : $"\n\n{result.NdiStatusMessage}";
            MessageBox.Show(
                $"This PC was onboarded successfully to {result.JobName}.\n\n{network}"
                + "\nNDI interface, group, and discovery settings were applied."
                + ndi,
                "NDI Configurator PC Agent setup complete",
                MessageBoxButtons.OK,
                result.NdiUpdateRequired ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Remote onboarding did not complete.\n\n{ex.Message}",
                "NDI Configurator PC Agent Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            ExitThread();
        }
    }
}
