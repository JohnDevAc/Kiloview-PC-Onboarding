namespace KiloviewPcOnboarding;

internal sealed class NetworkSelectionForm : Form
{
    private readonly ComboBox _networks = new();
    private readonly Label _details = UiTheme.Label(
        "Choose the adapter connected to the production NDI and device network.",
        9);
    private readonly Button _continue = UiTheme.Button("Use this adapter", true);

    public NetworkChoice? SelectedNetwork => _networks.SelectedItem as NetworkChoice;

    public NetworkSelectionForm(Icon icon)
    {
        Icon = icon;
        Text = "Kiloview PC Onboarding — Primary Network";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 330);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        Font = new Font("Segoe UI", 9);

        var title = UiTheme.Label("Choose the primary network adapter", 19, true);
        var subtitle = UiTheme.Label(
            "Discovery, registration, and all NDI configuration will be bound to this interface.",
            10);
        subtitle.ForeColor = UiTheme.Muted;
        var heading = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 104,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(26, 18, 26, 10),
            BackColor = UiTheme.Panel
        };
        heading.Controls.Add(title);
        heading.Controls.Add(subtitle);

        _networks.DropDownStyle = ComboBoxStyle.DropDownList;
        _networks.Dock = DockStyle.Top;
        _networks.Height = 38;
        _networks.BackColor = Color.FromArgb(10, 18, 17);
        _networks.ForeColor = UiTheme.Text;
        _details.ForeColor = UiTheme.Muted;
        _details.MaximumSize = new Size(640, 0);
        _continue.Enabled = false;

        var refresh = UiTheme.Button("Refresh adapters");
        refresh.Click += (_, _) => LoadNetworks();
        _networks.SelectedIndexChanged += (_, _) =>
        {
            if (SelectedNetwork is not { } selected)
            {
                _continue.Enabled = false;
                return;
            }
            var prefix = Math.Clamp(selected.PrefixLength, 24, 30);
            _details.Text =
                $"{selected.Address}/{selected.PrefixLength} · {selected.Description}\n"
                + $"Job Configurator discovery will scan only the selected /{prefix} subnet.";
            _continue.Enabled = true;
        };
        _continue.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = UiTheme.Button("Cancel");
        cancel.Click += (_, _) => Close();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 8)
        };
        actions.Controls.Add(_continue);
        actions.Controls.Add(cancel);
        actions.Controls.Add(refresh);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 26, 28, 14)
        };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 105,
            ColumnCount = 1,
            RowCount = 2
        };
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.Controls.Add(_networks, 0, 0);
        stack.Controls.Add(_details, 0, 1);
        content.Controls.Add(stack);
        content.Controls.Add(actions);
        Controls.Add(content);
        Controls.Add(heading);

        AcceptButton = _continue;
        CancelButton = cancel;
        LoadNetworks();
    }

    private void LoadNetworks()
    {
        _networks.BeginUpdate();
        _networks.Items.Clear();
        foreach (var network in NetworkService.GetChoices())
            _networks.Items.Add(network);
        _networks.EndUpdate();
        _networks.SelectedIndex = -1;
        _continue.Enabled = false;
        _details.Text = _networks.Items.Count == 0
            ? "No active IPv4 adapters were found. Connect the production network and select Refresh adapters."
            : "Select an adapter to continue. No network scan has started.";
        _details.ForeColor = _networks.Items.Count == 0 ? UiTheme.Amber : UiTheme.Muted;
    }
}
