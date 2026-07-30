using System.Diagnostics;

namespace KiloviewPcOnboarding;

internal sealed class MainForm : Form
{
    private readonly NdiToolsService _ndiTools = new();
    private readonly ComboBox _network = new();
    private readonly Label _ndiStatus = UiTheme.Label("Checking NDI Tools…", 10);
    private readonly ListBox _servers = new();
    private readonly Label _serverStatus = UiTheme.Label("Select a network adapter to begin.", 10);
    private readonly ActivityIndicator _networkActivity = new();
    private readonly ActivityIndicator _ndiActivity = new();
    private readonly ActivityIndicator _jobActivity = new();
    private readonly ToolTip _tooltips = new();
    private readonly Button _refreshNetwork = UiTheme.RefreshButton("Refresh network adapters");
    private readonly Button _ndiAction = UiTheme.RefreshButton("Check NDI Tools");
    private readonly Button _scan = UiTheme.Button("Scan network", true);
    private readonly Button _onboard = UiTheme.Button("Onboard this PC", true);
    private CancellationTokenSource? _operation;
    private NdiToolsStatus? _ndi;

    public MainForm(Icon icon)
    {
        UiTheme.ConfigureForm(this);
        Icon = icon;
        Text = "Kiloview PC Onboarding";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 360);
        Size = new Size(900, 620);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        Font = new Font("Segoe UI", 9);

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());
        LoadNetworks();
        WireEvents();
        Shown += async (_, _) =>
        {
            UiTheme.MaximizeIfNeeded(this);
            await CheckNdiAsync();
            if (_network.SelectedItem is NetworkChoice) await ScanAsync();
        };
        FormClosing += (_, _) => _operation?.Cancel();
        FormClosed += (_, _) => _tooltips.Dispose();
    }

    private Control BuildHeader()
    {
        var title = UiTheme.Label("Onboard this Windows PC", 22, true);
        var subtitle = UiTheme.Label(
            "Select the production network, verify NDI Tools, then join the local Kiloview job.",
            10);
        subtitle.ForeColor = UiTheme.Muted;
        var version = UiTheme.Label($"Utility v{NdiToolsService.UtilityVersion()} · EULA 1.0", 9);
        version.ForeColor = UiTheme.Green;
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 16, 24, 12),
            ColumnCount = 2,
            RowCount = 2,
            BackColor = UiTheme.Panel
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        title.Anchor = AnchorStyles.Left;
        subtitle.Anchor = AnchorStyles.Left;
        version.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(version, 1, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.SetColumnSpan(subtitle, 2);
        return header;
    }

    private Control BuildBody()
    {
        _network.Dock = DockStyle.Fill;
        _network.DropDownStyle = ComboBoxStyle.DropDownList;
        _network.BackColor = Color.FromArgb(10, 18, 17);
        _network.ForeColor = UiTheme.Text;
        _network.Height = 34;

        _ndiStatus.ForeColor = UiTheme.Muted;
        _ndiStatus.MaximumSize = new Size(680, 0);
        _tooltips.SetToolTip(_refreshNetwork, "Refresh network adapters");
        _tooltips.SetToolTip(_ndiAction, "Check NDI Tools again");

        _servers.Dock = DockStyle.Fill;
        _servers.BackColor = Color.FromArgb(10, 18, 17);
        _servers.ForeColor = UiTheme.Text;
        _servers.BorderStyle = BorderStyle.FixedSingle;
        _servers.Font = new Font("Segoe UI", 10);
        _servers.IntegralHeight = false;
        _serverStatus.ForeColor = UiTheme.Muted;

        _onboard.Width = 190;
        _onboard.Enabled = false;

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 18),
            ColumnCount = 2,
            RowCount = 2
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(Panel(
            "01",
            "Production network",
            _networkActivity,
            _refreshNetwork,
            NetworkControls(),
            new Padding(0, 0, 7, 14)), 0, 0);
        body.Controls.Add(Panel(
            "02",
            "NDI Tools",
            _ndiActivity,
            _ndiAction,
            NdiControls(),
            new Padding(7, 0, 0, 14)), 1, 0);
        var server = Panel("03", "Kiloview job", _jobActivity, null, ServerControls(), Padding.Empty);
        body.Controls.Add(server, 0, 1);
        body.SetColumnSpan(server, 2);
        return body;
    }

    private Control NetworkControls()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(_network, 0, 0);
        return row;
    }

    private Control NdiControls()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(_ndiStatus, 0, 0);
        return row;
    }

    private Control ServerControls()
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        container.Controls.Add(_servers, 0, 0);
        container.Controls.Add(_serverStatus, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_scan, 0, 0);
        actions.Controls.Add(_onboard, 2, 0);
        _onboard.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        container.Controls.Add(actions, 0, 2);
        return container;
    }

    private static Control Panel(
        string number,
        string title,
        ActivityIndicator activity,
        Button? refresh,
        Control content,
        Padding margin)
    {
        var numberLabel = UiTheme.Label(number, 9, true);
        numberLabel.ForeColor = UiTheme.Background;
        numberLabel.BackColor = UiTheme.Green;
        numberLabel.Padding = new Padding(7, 4, 7, 4);
        var heading = UiTheme.Label(title, 13, true);
        heading.Anchor = AnchorStyles.Left;
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = refresh is null ? 3 : 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        if (refresh is not null)
        {
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        }
        numberLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        activity.Anchor = AnchorStyles.Right;
        header.Controls.Add(numberLabel, 0, 0);
        header.Controls.Add(heading, 1, 0);
        header.Controls.Add(activity, 2, 0);
        if (refresh is not null)
        {
            refresh.Anchor = AnchorStyles.Right;
            header.Controls.Add(refresh, 4, 0);
        }
        content.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(content, 0, 1);
        var panel = new System.Windows.Forms.Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            Padding = new Padding(18, 14, 18, 14),
            Margin = margin
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.ClientSize.Width - 1, panel.ClientSize.Height - 1);
        };
        panel.Controls.Add(layout);
        return panel;
    }

    private void WireEvents()
    {
        _refreshNetwork.Click += (_, _) => LoadNetworks();
        _ndiAction.Click += async (_, _) =>
        {
            if (_ndi?.UpdateRequired == true) await InstallNdiAsync();
            else await CheckNdiAsync();
        };
        _scan.Click += async (_, _) => await ScanAsync();
        _servers.SelectedIndexChanged += (_, _) => UpdateReadyState();
        _network.SelectedIndexChanged += async (_, _) =>
        {
            if (_network.SelectedItem is NetworkChoice selected)
            {
                _networkActivity.State = ActivityState.Complete;
            }
            else
            {
                _networkActivity.State = ActivityState.Idle;
            }
            _servers.Items.Clear();
            _jobActivity.State = ActivityState.Idle;
            UpdateReadyState();
            if (_network.SelectedItem is NetworkChoice)
                await ScanAsync();
        };
        _onboard.Click += async (_, _) => await OnboardAsync();
    }

    private void LoadNetworks(NetworkChoice? requested = null)
    {
        _networkActivity.State = ActivityState.Working;
        var previous = requested?.Id ?? (_network.SelectedItem as NetworkChoice)?.Id;
        var previousAddress = requested?.Address ?? (_network.SelectedItem as NetworkChoice)?.Address;
        var choices = NetworkService.GetChoices();
        _network.BeginUpdate();
        _network.Items.Clear();
        foreach (var choice in choices) _network.Items.Add(choice);
        _network.EndUpdate();
        if (choices.Count > 0)
        {
            var preferred = choices.FirstOrDefault(choice =>
                    choice.Id == previous && choice.Address == previousAddress)
                ?? choices.FirstOrDefault(choice => choice.Id == previous)
                ?? choices.FirstOrDefault(choice => choice.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
                ?? choices[0];
            _network.SelectedItem = preferred;
            _networkActivity.State = ActivityState.Complete;
        }
        else
        {
            _serverStatus.Text = "No active IPv4 network adapters were found.";
            _serverStatus.ForeColor = UiTheme.Amber;
            _networkActivity.State = ActivityState.Error;
            _onboard.Enabled = false;
        }
    }

    private async Task CheckNdiAsync()
    {
        await RunBusyAsync(_ndiActivity, async token =>
        {
            _ndiStatus.Text = "Checking the installed and current official NDI Tools versions…";
            _ndi = await _ndiTools.CheckAsync(token);
            _ndiStatus.Text = _ndi.Message;
            _ndiStatus.ForeColor = _ndi.UpdateRequired ? UiTheme.Amber : UiTheme.Green;
            UpdateNdiActionHint();
        });
        UpdateReadyState();
    }

    private async Task InstallNdiAsync()
    {
        var result = MessageBox.Show(
            this,
            "The official NDI Tools installer will be downloaded from downloads.ndi.tv, its Windows signature will be verified, and its own licence window will open. Continue?",
            "Install official NDI Tools",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (result != DialogResult.OK) return;
        await RunBusyAsync(_ndiActivity, async token =>
        {
            _ndiStatus.ForeColor = UiTheme.Muted;
            _ndiStatus.Text = "Downloading the official NDI Tools installer…";
            await _ndiTools.DownloadAndInstallAsync(null, token);
            _ndi = await _ndiTools.CheckAsync(token);
            _ndiStatus.Text = _ndi.Message;
            _ndiStatus.ForeColor = _ndi.UpdateRequired ? UiTheme.Amber : UiTheme.Green;
            UpdateNdiActionHint();
        });
        UpdateReadyState();
    }

    private async Task ScanAsync()
    {
        if (_network.SelectedItem is not NetworkChoice network) return;
        await RunBusyAsync(_jobActivity, async token =>
        {
            _servers.Items.Clear();
            _serverStatus.ForeColor = UiTheme.Muted;
            _serverStatus.Text = $"Searching {ScanDescription(network)} for Kiloview Job Configurator…";
            var servers = await JobConfiguratorDiscovery.FindAsync(network, null, token);
            foreach (var server in servers) _servers.Items.Add(server);
            if (_servers.Items.Count == 1) _servers.SelectedIndex = 0;
            var compatible = servers.Count(server => server.SupportsRegistration);
            _serverStatus.Text = servers.Count switch
            {
                0 => "No active job was found. Check LAN access, the selected adapter, and the firewall profile.",
                _ when compatible == 0 => "Found Job Configurator, but it must be updated before this PC can register.",
                _ when compatible < servers.Count => $"Found {servers.Count} active jobs; update entries marked “update required” before use.",
                _ => $"Found {servers.Count} active job{(servers.Count == 1 ? "" : "s")}."
            };
            _serverStatus.ForeColor = servers.Count == 0 ? UiTheme.Amber : UiTheme.Green;
        });
        UpdateReadyState();
    }

    private async Task OnboardAsync()
    {
        if (_network.SelectedItem is not NetworkChoice network
            || _servers.SelectedItem is not JobConfiguratorInstance server
            || _ndi is null
            || _ndi.UpdateRequired)
            return;
        await RunBusyAsync(_jobActivity, async token =>
        {
            _serverStatus.ForeColor = UiTheme.Muted;
            _serverStatus.Text = "Applying the preferred interface, NDI group, and discovery server…";
            await NdiConfigurationService.ApplyAsync(network, server, token);
            if (!server.SupportsRegistration)
            {
                _serverStatus.ForeColor = UiTheme.Amber;
                _serverStatus.Text =
                    $"NDI settings were applied for {server.JobName}. Update Job Configurator to register this PC.";
                OpenJobConfiguratorKeepingFocus(server.BaseUri);
                MessageBox.Show(
                    this,
                    $"Local NDI settings were applied for {server.JobName}.\n\n"
                    + $"Preferred interface: {network.Address}\n"
                    + $"NDI discovery server: {server.NdiDiscoveryServerIp}\n"
                    + "NDI Discovery: Use Access Manager Settings\n\n"
                    + "Update Job Configurator before registering this PC.",
                    "NDI settings applied",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            var request = new RegistrationRequest(
                ConsentStore.EndpointId(),
                Environment.MachineName,
                network.Address,
                network.Name,
                network.PrefixLength,
                true,
                _ndi.InstalledVersion?.ToString() ?? "unknown",
                NdiToolsService.UtilityVersion(),
                "1.0");
            _serverStatus.Text = "Registering this PC with the selected job…";
            await JobConfiguratorDiscovery.RegisterAsync(network, server, request, token);
            _serverStatus.ForeColor = UiTheme.Green;
            _serverStatus.Text = $"{Environment.MachineName} is onboarded to {server.JobName}. Restart running NDI applications so they load the new settings.";
            OpenJobConfiguratorKeepingFocus(server.BaseUri);
            MessageBox.Show(
                this,
                $"This PC is now onboarded to {server.JobName}.\n\nPreferred interface: {network.Address}\nNDI discovery server: {server.NdiDiscoveryServerIp}\nNDI group: {server.JobName}\n\nRestart any running NDI applications.",
                "Onboarding complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
    }

    private void OpenJobConfiguratorKeepingFocus(Uri address)
    {
        Process.Start(new ProcessStartInfo(address.ToString()) { UseShellExecute = true });

        var attempts = 0;
        var focusTimer = new System.Windows.Forms.Timer { Interval = 250 };
        focusTimer.Tick += (_, _) =>
        {
            if (IsDisposed)
            {
                focusTimer.Dispose();
                return;
            }

            BringToFront();
            Activate();
            if (++attempts < 4)
                return;

            focusTimer.Stop();
            focusTimer.Dispose();
        };
        BringToFront();
        Activate();
        focusTimer.Start();
    }

    private void UpdateNdiActionHint()
    {
        var action = _ndi?.UpdateRequired == true
            ? _ndi.Installed ? "Update NDI Tools" : "Install NDI Tools"
            : "Check NDI Tools again";
        _ndiAction.AccessibleName = action;
        _tooltips.SetToolTip(_ndiAction, action);
    }

    private async Task RunBusyAsync(
        ActivityIndicator activity,
        Func<CancellationToken, Task> operation)
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        activity.State = ActivityState.Working;
        SetBusy(true);
        try
        {
            await operation(_operation.Token);
            activity.State = ActivityState.Complete;
        }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested)
        {
            activity.State = ActivityState.Idle;
            _serverStatus.Text = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            activity.State = ActivityState.Error;
            _serverStatus.ForeColor = UiTheme.Red;
            _serverStatus.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Kiloview PC Onboarding", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        var selectedServer = _servers.SelectedItem as JobConfiguratorInstance;
        _onboard.Text = selectedServer is { SupportsRegistration: false }
            ? "Apply NDI settings"
            : "Onboard this PC";
        UseWaitCursor = busy;
        _network.Enabled = !busy;
        _refreshNetwork.Enabled = !busy;
        _ndiAction.Enabled = !busy;
        _scan.Enabled = !busy;
        _servers.Enabled = !busy;
        _onboard.Enabled = !busy
            && _network.SelectedItem is NetworkChoice
            && selectedServer is not null
            && _ndi?.UpdateRequired == false;
    }

    private void UpdateReadyState() => SetBusy(false);

    private static string ScanDescription(NetworkChoice network)
    {
        var prefix = Math.Clamp(network.PrefixLength, 24, 30);
        var parts = network.Address.Split('.');
        return prefix == 24 && parts.Length == 4
            ? $"{parts[0]}.{parts[1]}.{parts[2]}.0/24"
            : $"the selected /{prefix} subnet";
    }
}
