namespace KiloviewPcAgent;

internal sealed class AgentStatusForm : Form
{
    private readonly Func<AgentConfiguration> _configuration;
    private readonly Func<bool> _networkAvailable;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Label _summary = ValueLabel();
    private readonly Label _computer = ValueLabel();
    private readonly Label _version = ValueLabel();
    private readonly Label _network = ValueLabel();
    private readonly Label _jobs = ValueLabel();
    private readonly Label _ndiTools = ValueLabel();
    private readonly Label _multicast = ValueLabel();
    private readonly Label _updated = ValueLabel();

    public AgentStatusForm(
        Icon icon,
        Func<AgentConfiguration> configuration,
        Func<bool> networkAvailable)
    {
        _configuration = configuration;
        _networkAvailable = networkAvailable;

        Text = "Kiloview PC Agent Status";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 455);
        MinimumSize = new Size(500, 430);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Padding.Empty
        };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        page.Controls.Add(Header(), 0, 0);
        page.Controls.Add(Details(), 0, 1);
        page.Controls.Add(Footer(), 0, 2);
        Controls.Add(page);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        RefreshStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    public void RefreshStatus()
    {
        if (IsDisposed)
            return;

        var configuration = AgentStore.Read() ?? _configuration();
        var ndi = AgentMonitor.FindNdiTools();
        MulticastConfigurationState? multicast;
        try
        {
            multicast = AgentMulticastService.Current(configuration);
        }
        catch (AgentApiException)
        {
            multicast = null;
        }
        var available = _networkAvailable();

        _summary.Text = available
            ? "Online and ready for remote management"
            : "The selected network interface is unavailable";
        _summary.ForeColor = available ? Color.FromArgb(24, 112, 64) : Color.FromArgb(176, 62, 48);
        _computer.Text = Environment.MachineName;
        _version.Text = AgentMonitor.Version();
        _network.Text = $"{configuration.AdapterName} - {configuration.Address}/{configuration.PrefixLength}";
        _jobs.Text = JobSummary(configuration.Memberships);
        _ndiTools.Text = ndi.Installed
            ? $"Installed{(string.IsNullOrWhiteSpace(ndi.Version) ? "" : $" - version {ndi.Version}")}"
            : "Not detected - install the latest NDI Tools";
        _multicast.Text = multicast is null ? "Settings need attention" : MulticastSummary(multicast);
        _updated.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    private Control Header()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(232, 247, 250) };
        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.FromArgb(25, 48, 56),
            Location = new Point(24, 18),
            Text = "PC Agent status"
        };
        _summary.AutoSize = true;
        _summary.Font = new Font("Segoe UI", 10F);
        _summary.Location = new Point(26, 58);
        panel.Controls.Add(title);
        panel.Controls.Add(_summary);
        return panel;
    }

    private Control Details()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(24, 18, 24, 12)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < table.RowCount; index++)
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / table.RowCount));

        AddRow(table, 0, "Computer", _computer);
        AddRow(table, 1, "Agent version", _version);
        AddRow(table, 2, "Network", _network);
        AddRow(table, 3, "Onboarded jobs", _jobs);
        AddRow(table, 4, "NDI Tools", _ndiTools);
        AddRow(table, 5, "NDI transport", _multicast);
        AddRow(table, 6, "Last checked", _updated);
        return table;
    }

    private Control Footer()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(245, 246, 247),
            Padding = new Padding(0, 14, 22, 13)
        };
        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Size = new Size(96, 34),
            Dock = DockStyle.Right
        };
        close.Click += (_, _) => Close();
        CancelButton = close;
        panel.Controls.Add(close);
        return panel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string name, Label value)
    {
        var label = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(88, 96, 100),
            Text = name
        };
        value.Anchor = AnchorStyles.Left;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static Label ValueLabel() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(330, 42),
        ForeColor = Color.FromArgb(28, 33, 36)
    };

    private static string JobSummary(IReadOnlyList<AgentMembership> memberships)
    {
        if (memberships.Count == 0)
            return "None";
        var names = memberships.Select(item => item.JobName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length <= 2)
            return string.Join(", ", names);
        return $"{string.Join(", ", names.Take(2))} and {names.Length - 2} more";
    }

    private static string MulticastSummary(MulticastConfigurationState state)
    {
        if (!state.InUse)
            return "Settings need attention";
        if (string.Equals(state.Mode, "unicast", StringComparison.OrdinalIgnoreCase))
            return "Unicast";
        return $"Multicast - {state.NetPrefix}/24 - TTL {state.Ttl}";
    }
}
