using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KiloviewPcAgent;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Control _dispatcher = new();
    private AgentConfiguration _configuration;
    private AgentNetworkHost? _networkHost;

    public AgentApplicationContext(Icon icon)
    {
        _configuration = AgentStore.Read()
            ?? throw new InvalidOperationException(
                "Kiloview PC Agent has not been configured. Run Kiloview PC Onboarding first.");
        _dispatcher.CreateControl();
        _tray = new NotifyIcon
        {
            Icon = icon,
            Text = "Kiloview PC Agent",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenStatusInBrowser();
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (_, _) => ReloadConfiguration();
        _refreshTimer.Start();
        BuildMenu();
        StartNetworkHost();
    }

    protected override void ExitThreadCore()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _networkHost?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _dispatcher.Dispose();
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
            _networkHost = new AgentNetworkHost(() => _configuration, ConfirmRemoteLaunchAsync);
            _networkHost.Start();
            _tray.Text = Truncate($"Kiloview PC Agent · {_configuration.Address}", 63);
        }
        catch (Exception ex)
        {
            _networkHost?.Dispose();
            _networkHost = null;
            _tray.Text = "Kiloview PC Agent · network unavailable";
            _tray.ShowBalloonTip(
                5000,
                "Kiloview PC Agent",
                $"The selected interface could not be opened: {ex.Message}",
                ToolTipIcon.Warning);
        }
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(
            $"Online · {_configuration.AdapterName} · {_configuration.Address}/{_configuration.PrefixLength}")
        {
            Enabled = false
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open agent status", null, (_, _) => OpenStatusInBrowser());

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
                    $"{membership.JobName} · {membership.ServerAddress}");
                server.DropDownItems.Add(
                    "Open Job Configurator",
                    null,
                    (_, _) => OpenUrl(membership.BaseUri));
                server.DropDownItems.Add(
                    "Remove this PC from job…",
                    null,
                    async (_, _) => await RemoveMembershipAsync(membership));
                memberships.DropDownItems.Add(server);
            }
        }
        menu.Items.Add(memberships);
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
            var server = $"{request.ServerName ?? "Kiloview Job Configurator"} ({request.RemoteAddress})";
            var job = string.IsNullOrWhiteSpace(request.JobName) ? "" : $"\nJob: {request.JobName}";
            var choice = MessageBox.Show(
                $"{server} is requesting permission to apply centrally managed onboarding settings to this PC.{job}\n\n"
                + "If approved, Windows will request administrator permission. NDI settings and the selected adapter's IPv4, gateway, and DNS settings may change.\n\nAllow this request?",
                "Kiloview PC Agent",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            var launched = choice == DialogResult.Yes
                && OpenRemoteOnboardingUtility(
                    request.ConfiguratorUrl,
                    request.RemoteAddress,
                    _configuration.EndpointId);
            completion.SetResult(launched);
        });
        return completion.Task;
    }

    private async Task RemoveMembershipAsync(AgentMembership membership)
    {
        var choice = MessageBox.Show(
            $"Remove {Environment.MachineName} from {membership.JobName} on {membership.ServerAddress}?\n\n"
            + "This removes the Job Configurator registration. Local NDI settings and the PC Agent remain installed.",
            "Remove from Kiloview job",
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
                "Kiloview PC Agent",
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
                "Kiloview PC Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OpenStatusInBrowser() => OpenUrl(
        $"http://{_configuration.Address}:{AgentNetworkHost.ApiPort}/api/v1/status");

    private static void OpenUrl(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Kiloview PC Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool OpenRemoteOnboardingUtility(
        string? configuratorUrl,
        string requestingAddress,
        string endpointId)
    {
        try
        {
            var utility = Path.Combine(AppContext.BaseDirectory, "Kiloview PC Onboarding.exe");
            if (!File.Exists(utility))
                throw new FileNotFoundException("The installed PC Onboarding utility was not found.", utility);
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
            MessageBox.Show(ex.Message, "Kiloview PC Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
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
