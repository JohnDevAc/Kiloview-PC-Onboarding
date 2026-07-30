using System.Diagnostics;

namespace KiloviewPcOnboarding;

internal sealed class MainForm : Form
{
    private readonly NdiToolsService _ndiTools = new();
    private readonly ComboBox _network = new();
    private readonly Label _ndiStatus = UiTheme.Label("Checking NDI Tools…", 10);
    private readonly Label _networkHint = UiTheme.Label("", 9);
    private readonly ListBox _servers = new();
    private readonly Label _serverStatus = UiTheme.Label("Select a network adapter to begin.", 10);
    private readonly ProgressBar _progress = new();
    private readonly Button _refreshNetwork = UiTheme.Button("Refresh adapters");
    private readonly Button _ndiAction = UiTheme.Button("Check NDI Tools");
    private readonly Button _scan = UiTheme.Button("Scan network", true);
    private readonly Button _onboard = UiTheme.Button("Onboard this PC", true);
    private readonly CheckBox _openJob = new();
    private CancellationTokenSource? _operation;
    private NdiToolsStatus? _ndi;

    public MainForm(Icon icon, NetworkChoice initialNetwork)
    {
        Icon = icon;
        Text = "Kiloview PC Onboarding";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 690);
        Size = new Size(1000, 760);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        Font = new Font("Segoe UI", 9);

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());
        LoadNetworks(initialNetwork);
        WireEvents();
        Shown += async (_, _) =>
        {
            await CheckNdiAsync();
            if (_network.SelectedItem is NetworkChoice) await ScanAsync();
        };
        FormClosing += (_, _) => _operation?.Cancel();
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
            Height = 104,
            Padding = new Padding(28, 18, 28, 12),
            ColumnCount = 2,
            RowCount = 2,
            BackColor = UiTheme.Panel
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var copy = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        copy.Controls.Add(title);
        copy.Controls.Add(subtitle);
        header.Controls.Add(copy, 0, 0);
        header.SetRowSpan(copy, 2);
        header.Controls.Add(version, 1, 0);
        return header;
    }

    private Control BuildBody()
    {
        _network.Dock = DockStyle.Fill;
        _network.DropDownStyle = ComboBoxStyle.DropDownList;
        _network.BackColor = Color.FromArgb(10, 18, 17);
        _network.ForeColor = UiTheme.Text;
        _network.Height = 34;
        _networkHint.ForeColor = UiTheme.Muted;

        _ndiStatus.ForeColor = UiTheme.Muted;
        _ndiStatus.MaximumSize = new Size(680, 0);
        _ndiAction.Width = 170;

        _servers.Dock = DockStyle.Fill;
        _servers.BackColor = Color.FromArgb(10, 18, 17);
        _servers.ForeColor = UiTheme.Text;
        _servers.BorderStyle = BorderStyle.FixedSingle;
        _servers.Font = new Font("Segoe UI", 10);
        _servers.IntegralHeight = false;
        _serverStatus.ForeColor = UiTheme.Muted;

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;

        _openJob.Text = "Open the Job Configurator after onboarding";
        _openJob.Checked = true;
        _openJob.AutoSize = true;
        _openJob.ForeColor = UiTheme.Text;
        _onboard.Width = 190;
        _onboard.Enabled = false;

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 24),
            ColumnCount = 1,
            RowCount = 3
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(Panel("01", "Production network", "All discovery and NDI traffic will use this adapter.",
            NetworkControls()), 0, 0);
        body.Controls.Add(Panel("02", "NDI Tools", "The official NDI installer is used when an update is required.",
            NdiControls()), 0, 1);
        body.Controls.Add(Panel("03", "Kiloview job", "The utility scans this adapter for Job Configurator on TCP 8091.",
            ServerControls()), 0, 2);
        return body;
    }

    private Control NetworkControls()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_network, 0, 0);
        row.Controls.Add(_refreshNetwork, 1, 0);
        row.Controls.Add(_networkHint, 0, 1);
        row.SetColumnSpan(_networkHint, 2);
        return row;
    }

    private Control NdiControls()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_ndiStatus, 0, 0);
        row.Controls.Add(_ndiAction, 1, 0);
        row.Controls.Add(_progress, 0, 1);
        row.SetColumnSpan(_progress, 2);
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
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        container.Controls.Add(_servers, 0, 0);
        container.Controls.Add(_serverStatus, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_scan, 0, 0);
        actions.Controls.Add(_openJob, 1, 0);
        actions.Controls.Add(_onboard, 2, 0);
        container.Controls.Add(actions, 0, 2);
        return container;
    }

    private static Control Panel(string number, string title, string subtitle, Control content)
    {
        var numberLabel = UiTheme.Label(number, 9, true);
        numberLabel.ForeColor = UiTheme.Background;
        numberLabel.BackColor = UiTheme.Green;
        numberLabel.Padding = new Padding(7, 4, 7, 4);
        var heading = UiTheme.Label(title, 13, true);
        var note = UiTheme.Label(subtitle, 9);
        note.ForeColor = UiTheme.Muted;
        var copy = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        copy.Controls.Add(heading);
        copy.Controls.Add(note);
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 10)
        };
        header.Controls.Add(numberLabel);
        header.Controls.Add(copy);
        content.Dock = DockStyle.Fill;
        var panel = new System.Windows.Forms.Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            Padding = new Padding(20),
            Margin = new Padding(0, 0, 0, 14)
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.ClientSize.Width - 1, panel.ClientSize.Height - 1);
        };
        panel.Controls.Add(content);
        panel.Controls.Add(header);
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
        _network.SelectedIndexChanged += (_, _) =>
        {
            if (_network.SelectedItem is NetworkChoice selected)
                _networkHint.Text = $"Scanning {ScanDescription(selected)}; traffic is bound to {selected.Address}.";
            _servers.Items.Clear();
            UpdateReadyState();
        };
        _onboard.Click += async (_, _) => await OnboardAsync();
    }

    private void LoadNetworks(NetworkChoice? requested = null)
    {
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
        }
        else
        {
            _networkHint.Text = "No active IPv4 network adapters were found.";
            _onboard.Enabled = false;
        }
    }

    private async Task CheckNdiAsync()
    {
        await RunBusyAsync(async token =>
        {
            _ndiStatus.Text = "Checking the installed and current official NDI Tools versions…";
            _ndi = await _ndiTools.CheckAsync(token);
            _ndiStatus.Text = _ndi.Message;
            _ndiStatus.ForeColor = _ndi.UpdateRequired ? UiTheme.Amber : UiTheme.Green;
            _ndiAction.Text = _ndi.UpdateRequired
                ? _ndi.Installed ? "Update NDI Tools" : "Install NDI Tools"
                : "Check again";
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
        await RunBusyAsync(async token =>
        {
            _progress.Visible = true;
            _progress.Value = 0;
            _ndiStatus.ForeColor = UiTheme.Muted;
            _ndiStatus.Text = "Downloading the official NDI Tools installer…";
            var progress = new Progress<int>(value => _progress.Value = Math.Clamp(value, 0, 100));
            await _ndiTools.DownloadAndInstallAsync(progress, token);
            _progress.Visible = false;
            _ndi = await _ndiTools.CheckAsync(token);
            _ndiStatus.Text = _ndi.Message;
            _ndiStatus.ForeColor = _ndi.UpdateRequired ? UiTheme.Amber : UiTheme.Green;
            _ndiAction.Text = _ndi.UpdateRequired ? "Update NDI Tools" : "Check again";
        });
        UpdateReadyState();
    }

    private async Task ScanAsync()
    {
        if (_network.SelectedItem is not NetworkChoice network) return;
        await RunBusyAsync(async token =>
        {
            _servers.Items.Clear();
            _serverStatus.ForeColor = UiTheme.Muted;
            _serverStatus.Text = $"Searching {ScanDescription(network)} for Kiloview Job Configurator…";
            _progress.Visible = true;
            _progress.Value = 0;
            var progress = new Progress<int>(value => _progress.Value = Math.Clamp(value, 0, 100));
            var servers = await JobConfiguratorDiscovery.FindAsync(network, progress, token);
            foreach (var server in servers) _servers.Items.Add(server);
            if (_servers.Items.Count == 1) _servers.SelectedIndex = 0;
            _serverStatus.Text = servers.Count == 0
                ? "No active job was found. Check that the main application is running with LAN access enabled."
                : $"Found {servers.Count} active job{(servers.Count == 1 ? "" : "s")}.";
            _serverStatus.ForeColor = servers.Count == 0 ? UiTheme.Amber : UiTheme.Green;
            _progress.Visible = false;
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
        await RunBusyAsync(async token =>
        {
            _serverStatus.ForeColor = UiTheme.Muted;
            _serverStatus.Text = "Applying the preferred interface, NDI group, and discovery server…";
            await NdiConfigurationService.ApplyAsync(network, server, token);
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
            if (_openJob.Checked)
                Process.Start(new ProcessStartInfo(server.BaseUri.ToString()) { UseShellExecute = true });
            MessageBox.Show(
                this,
                $"This PC is now onboarded to {server.JobName}.\n\nPreferred interface: {network.Address}\nNDI discovery server: {server.NdiDiscoveryServerIp}\nNDI group: {server.JobName}\n\nRestart any running NDI applications.",
                "Onboarding complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> operation)
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await operation(_operation.Token);
        }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested)
        {
            _serverStatus.Text = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            _progress.Visible = false;
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
        UseWaitCursor = busy;
        _network.Enabled = !busy;
        _refreshNetwork.Enabled = !busy;
        _ndiAction.Enabled = !busy;
        _scan.Enabled = !busy;
        _servers.Enabled = !busy;
        _onboard.Enabled = !busy
            && _network.SelectedItem is NetworkChoice
            && _servers.SelectedItem is JobConfiguratorInstance
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
