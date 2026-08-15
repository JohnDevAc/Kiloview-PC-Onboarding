using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KiloviewPcAgent;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _activationTimer;
    private readonly WaitHandle _showStatusRequest;
    private readonly Control _dispatcher = new();
    private readonly CancellationTokenSource _lifetime = new();
    private AgentConfiguration _configuration;
    private AgentNetworkHost? _networkHost;
    private AgentStatusForm? _statusWindow;
    private ToolStripMenuItem? _updateMenuItem;
    private bool _remoteLaunchPending;
    private bool _updatePending;

    public AgentApplicationContext(Icon icon, WaitHandle showStatusRequest)
    {
        _showStatusRequest = showStatusRequest;
        _configuration = AgentStore.Read()
            ?? throw new InvalidOperationException(
                "NDI Configurator PC Agent has not been configured. Run NDI Configurator PC Agent Setup first.");
        _dispatcher.CreateControl();
        _tray = new NotifyIcon
        {
            Icon = icon,
            Text = "NDI Configurator PC Agent",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowStatusWindow();
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (_, _) => ReloadConfiguration();
        _refreshTimer.Start();
        _activationTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _activationTimer.Tick += (_, _) =>
        {
            if (_showStatusRequest.WaitOne(0))
                ShowStatusWindow();
        };
        _activationTimer.Start();
        BuildMenu();
        StartNetworkHost();
    }

    protected override void ExitThreadCore()
    {
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _activationTimer.Stop();
        _activationTimer.Dispose();
        _networkHost?.Dispose();
        _statusWindow?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _dispatcher.Dispose();
        _lifetime.Dispose();
        base.ExitThreadCore();
    }

    private void ReloadConfiguration()
    {
        var updated = AgentStore.Read();
        if (updated is null)
            return;
        if (updated.UpdatedUtc == _configuration.UpdatedUtc)
        {
            if (_networkHost is null)
                StartNetworkHost();
            return;
        }
        var previousNetwork = NetworkSignature(_configuration);
        _configuration = updated;
        BuildMenu();
        if (!string.Equals(previousNetwork, NetworkSignature(updated), StringComparison.Ordinal))
            StartNetworkHost();
    }

    private void StartNetworkHost()
    {
        _networkHost?.Dispose();
        _networkHost = null;
        try
        {
            _networkHost = new AgentNetworkHost(
                () => _configuration,
                ConfirmRemoteLaunchAsync,
                launchApproved: LaunchApprovedRemoteOnboarding);
            _networkHost.Start();
            _tray.Text = Truncate($"NDI Configurator PC Agent - {_configuration.Address}", 63);
        }
        catch (Exception ex)
        {
            _networkHost?.Dispose();
            _networkHost = null;
            _tray.Text = "NDI Configurator PC Agent - network unavailable";
            _tray.ShowBalloonTip(
                5000,
                "NDI Configurator PC Agent",
                $"The selected interface could not be opened: {ex.Message}",
                ToolTipIcon.Warning);
        }
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(
            $"Online - {_configuration.AdapterName} - {_configuration.Address}/{_configuration.PrefixLength}")
        {
            Enabled = false
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Status", null, (_, _) => ShowStatusWindow());

        var memberships = new ToolStripMenuItem("Onboarded jobs");
        if (_configuration.Memberships.Count == 0)
        {
            memberships.DropDownItems.Add(new ToolStripMenuItem("No onboarded jobs") { Enabled = false });
        }
        else
        {
            foreach (var membership in _configuration.Memberships
                .OrderBy(item => item.JobName, StringComparer.OrdinalIgnoreCase))
            {
                var server = new ToolStripMenuItem(
                    $"{membership.JobName} - {membership.ServerAddress}");
                server.DropDownItems.Add(
                    "Open Job Configurator",
                    null,
                    (_, _) => OpenUrl(membership.BaseUri));
                server.DropDownItems.Add(
                    "Remove this PC from job...",
                    null,
                    async (_, _) => await RemoveMembershipAsync(membership));
                memberships.DropDownItems.Add(server);
            }
        }
        menu.Items.Add(memberships);
        menu.Items.Add(new ToolStripSeparator());
        _updateMenuItem = new ToolStripMenuItem(
            _updatePending ? "Checking for updates…" : "Check for updates");
        _updateMenuItem.Enabled = !_updatePending && !_remoteLaunchPending;
        _updateMenuItem.Click += async (_, _) => await CheckForUpdatesAsync();
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit agent", null, (_, _) => ExitThread());

        var previous = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        previous?.Dispose();
    }

    private Task<bool> ConfirmRemoteLaunchAsync(OnboardingLaunchRequest request)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(() =>
        {
            var server = $"{request.ServerName ?? "NDI Job Configurator"} ({request.RemoteAddress})";
            var job = string.IsNullOrWhiteSpace(request.JobName) ? "" : $"\nJob: {request.JobName}";
            var choice = MessageBox.Show(
                $"{server} is requesting permission to apply centrally managed onboarding settings to this PC.{job}\n\n"
                + "If approved, Windows will request administrator permission. NDI settings and the selected adapter's IPv4, gateway, and DNS settings may change.\n\nAllow this request?",
                "NDI Configurator PC Agent",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes)
            {
                completion.SetResult(false);
                return;
            }
            if (_updatePending)
            {
                MessageBox.Show(
                    "An agent update is currently being prepared. Retry onboarding after the update completes.",
                    "NDI Configurator PC Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                completion.SetResult(false);
                return;
            }
            if (_remoteLaunchPending)
            {
                MessageBox.Show(
                    "Another remote onboarding request is already starting.",
                    "NDI Configurator PC Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                completion.SetResult(false);
                return;
            }

            _remoteLaunchPending = true;
            if (_updateMenuItem is not null)
                _updateMenuItem.Enabled = false;
            completion.SetResult(true);
        });
        return completion.Task;
    }

    private void LaunchApprovedRemoteOnboarding(OnboardingLaunchRequest request)
    {
        if (_dispatcher.IsDisposed)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _ = OpenRemoteOnboardingUtility(
                    request.ConfiguratorUrl,
                    request.RemoteAddress,
                    _configuration.EndpointId);
            }
            finally
            {
                _remoteLaunchPending = false;
                if (_updateMenuItem is not null)
                    _updateMenuItem.Enabled = true;
            }
        });
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updatePending || _remoteLaunchPending)
            return;
        _updatePending = true;
        SetUpdateMenuState("Checking for updates…", false);
        try
        {
            var check = await AgentUpdateService.CheckAsync(_lifetime.Token);
            if (!check.UpdateAvailable)
            {
                MessageBox.Show(
                    $"NDI Configurator PC Agent {AgentMonitor.Version()} is up to date.",
                    "NDI Configurator PC Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var choice = MessageBox.Show(
                $"NDI Configurator PC Agent {check.Release.Version} is available.\n\n"
                + "Download and install it now? Windows will request administrator permission after the package is verified.",
                "NDI Configurator PC Agent Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (choice != DialogResult.Yes)
                return;

            SetUpdateMenuState("Downloading update…", false);
            _tray.ShowBalloonTip(
                4000,
                "NDI Configurator PC Agent Update",
                $"Downloading and verifying version {check.Release.Version}.",
                ToolTipIcon.Info);
            var setupPath = await AgentUpdateService.DownloadAndStageAsync(
                check.Release,
                _lifetime.Token);
            Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            ExitThread();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(
                "The update was downloaded and verified, but administrator approval was canceled.",
                "NDI Configurator PC Agent Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            MessageBox.Show(
                $"The update could not be installed.\n\n{ex.Message}",
                "NDI Configurator PC Agent Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _updatePending = false;
            SetUpdateMenuState("Check for updates", !_remoteLaunchPending);
        }
    }

    private void SetUpdateMenuState(string text, bool enabled)
    {
        if (_updateMenuItem is null || _updateMenuItem.IsDisposed)
            return;
        _updateMenuItem.Text = text;
        _updateMenuItem.Enabled = enabled;
    }

    private async Task RemoveMembershipAsync(AgentMembership membership)
    {
        var choice = MessageBox.Show(
            $"Remove {Environment.MachineName} from {membership.JobName} on {membership.ServerAddress}?\n\n"
            + "This removes the Job Configurator registration. Local NDI settings and the PC Agent remain installed.",
            "Remove from NDI job",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes)
            return;

        try
        {
            using var client = CreateBoundClient(_configuration.Address);
            using var response = await client.DeleteAsync(
                new Uri(new Uri(membership.BaseUri), $"/api/pc-onboarding/{Uri.EscapeDataString(_configuration.EndpointId)}"));
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"Job Configurator returned {(int)response.StatusCode}."
                        : error);
            }

            AgentStore.RemoveMembership(membership.ServerAddress);
            _configuration = AgentStore.Read() ?? _configuration;
            BuildMenu();
            _tray.ShowBalloonTip(
                4000,
                "NDI Configurator PC Agent",
                $"This PC was removed from {membership.JobName}.",
                ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or IOException
            or InvalidOperationException)
        {
            MessageBox.Show(
                $"The PC could not be removed from {membership.JobName}.\n\n{ex.Message}",
                "NDI Configurator PC Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow is { IsDisposed: false })
        {
            _statusWindow.RefreshStatus();
            if (_statusWindow.WindowState == FormWindowState.Minimized)
                _statusWindow.WindowState = FormWindowState.Normal;
            _statusWindow.Activate();
            _statusWindow.BringToFront();
            return;
        }

        _statusWindow = new AgentStatusForm(
            _tray.Icon!,
            () => _configuration,
            () => _networkHost is not null);
        _statusWindow.FormClosed += (_, _) => _statusWindow = null;
        _statusWindow.Show();
        _statusWindow.Activate();
    }

    private static void OpenUrl(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NDI Configurator PC Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool OpenRemoteOnboardingUtility(
        string? configuratorUrl,
        string requestingAddress,
        string endpointId)
    {
        try
        {
            var utility = ResolveOnboardingUtility();
            if (!File.Exists(utility))
                throw new FileNotFoundException("The installed NDI Configurator PC Agent Setup utility was not found.", utility);
            var start = new ProcessStartInfo(utility)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            if (string.IsNullOrWhiteSpace(configuratorUrl))
                throw new InvalidOperationException("The requesting Configurator URL is missing.");
            start.ArgumentList.Add("--remote-onboarding");
            start.ArgumentList.Add("--configurator");
            start.ArgumentList.Add(configuratorUrl);
            start.ArgumentList.Add("--requesting-address");
            start.ArgumentList.Add(requestingAddress);
            start.ArgumentList.Add("--endpoint-id");
            start.ArgumentList.Add(endpointId);
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NDI Configurator PC Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static string ResolveOnboardingUtility()
    {
        var adjacent = Path.Combine(AppContext.BaseDirectory, "NDI Configurator PC Agent Setup.exe");
        if (File.Exists(adjacent))
            return adjacent;
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI Configurator",
            "PC Agent",
            "NDI Configurator PC Agent Setup.exe");
        if (File.Exists(installed))
            return installed;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Kiloview",
            "PC Agent",
            "Kiloview PC Onboarding.exe");
    }

    private static HttpClient CreateBoundClient(string localAddress)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static string NetworkSignature(AgentConfiguration value) =>
        $"{value.AdapterId}|{value.Address}|{value.PrefixLength}";

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
