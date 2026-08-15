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
    private readonly Button _scan = UiTheme.RefreshButton("Rescan for Job Configurator");
    private readonly Button _onboard = UiTheme.CompletionButton("REMOTE ONLY");
    private CancellationTokenSource? _operation;
    private NdiToolsStatus? _ndi;
    private AgentInstallationResult? _agentInstallation;

    public MainForm(Icon icon)
    {
        UiTheme.ConfigureForm(this);
        Icon = icon;
        Text = "NDI Configurator PC Agent Setup";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 360);
        Size = new Size(900, 620);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        Font = new Font("Segoe UI", 9);

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());
        LoadNetworks(AgentInstallationService.PreferredNetwork());
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
        var title = UiTheme.Label("NDI Configurator PC Agent", 22, true);
        var subtitle = UiTheme.Label(
            "Select the production network and install the agent. Job onboarding is then initiated remotely.",
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
        _tooltips.SetToolTip(_scan, "Rescan for Job Configurator");

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
        var server = Panel("03", "Job Configurator", _jobActivity, _scan, ServerControls(), Padding.Empty);
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
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        container.Controls.Add(_servers, 0, 0);
        container.Controls.Add(_serverStatus, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_onboard, 1, 0);
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
            if (_network.SelectedItem is NetworkChoice && _ndi?.UpdateRequired == false)
                await ScanAsync();
        };
        _scan.Click += async (_, _) => await ScanAsync();
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
            // Agent installation is independent of NDI Tools availability. Remote
            // onboarding reports missing or outdated NDI Tools after applying settings.
            {
                _serverStatus.Text = "Installing or updating NDI Configurator PC Agent…";
                _agentInstallation = await Task.Run(
                    () => AgentInstallationService.InstallOrUpdate(network),
                    token);
            }
            _serverStatus.Text = $"Searching {ScanDescription(network)} for NDI Job Configurator…";
            var servers = await JobConfiguratorDiscovery.FindAsync(network, null, token);
            var registeredAddresses = await JobConfiguratorDiscovery.FindExistingRegistrationsAsync(
                network,
                servers,
                ConsentStore.EndpointId(),
                Environment.MachineName,
                token);
            var displayedServers = servers
                .Select(server => server with
                {
                    AlreadyOnboarded = registeredAddresses.Contains(server.Address)
                })
                .ToArray();
            if (_agentInstallation is { Installed: true })
            {
                foreach (var existing in displayedServers.Where(server => server.AlreadyOnboarded))
                    AgentInstallationService.RecordMembership(network, existing);
            }
            foreach (var server in displayedServers)
                _servers.Items.Add(server);
            if (_servers.Items.Count == 1)
                _servers.SelectedIndex = 0;
            var compatible = displayedServers.Count(server => server.SupportsRegistration);
            var existingJobs = displayedServers
                .Where(server => server.AlreadyOnboarded)
                .Select(server => server.JobName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _serverStatus.Text = existingJobs.Length > 0
                ? $"Already onboarded: {string.Join(", ", existingJobs)}. Select a job to update its registration."
                : displayedServers.Length switch
                {
                    0 => "No active job was found. Check LAN access, the selected adapter, and the firewall profile.",
                    _ when compatible == 0 => "Found Job Configurator, but it must be updated before this PC can register.",
                    _ when compatible < displayedServers.Length => $"Found {displayedServers.Length} active jobs; update entries marked “update required” before use.",
                    _ => $"Found {displayedServers.Length} active job{(displayedServers.Length == 1 ? "" : "s")}."
                };
            if (_agentInstallation is { Installed: false })
                _serverStatus.Text = $"WARNING: {_agentInstallation.Message} {_serverStatus.Text}";
            else if (_agentInstallation is { Installed: true })
                _serverStatus.Text = $"NDI Configurator PC Agent ready. Start onboarding from Job Configurator. {_serverStatus.Text}";
            _serverStatus.ForeColor = displayedServers.Length == 0
                || _agentInstallation is { Installed: false }
                    ? UiTheme.Amber
                    : UiTheme.Green;
        });
        if (_agentInstallation is { Installed: false })
            _jobActivity.State = ActivityState.Error;
        UpdateReadyState();
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
            MessageBox.Show(this, ex.Message, "NDI Configurator PC Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _onboard.Text = _agentInstallation is { Installed: true }
            ? "AGENT READY"
            : "REMOTE ONLY";
        UseWaitCursor = busy;
        _network.Enabled = !busy;
        _refreshNetwork.Enabled = !busy;
        _ndiAction.Enabled = !busy;
        _scan.Enabled = !busy;
        _servers.Enabled = !busy;
        _onboard.Enabled = false;
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
