using System.Reflection;

namespace KiloviewPcOnboarding;

internal sealed class EulaForm : Form
{
    public EulaForm(Icon icon)
    {
        UiTheme.ConfigureForm(this);
        Icon = icon;
        Text = "Kiloview PC Onboarding — End User Licence Agreement";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 380);
        Size = new Size(700, 460);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;

        var title = UiTheme.Label("Kiloview PC Onboarding", 20, true);
        var subtitle = UiTheme.Label("Review and accept the licence before configuring this Windows PC.", 10);
        subtitle.ForeColor = UiTheme.Muted;
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(22, 14, 22, 8),
            BackColor = UiTheme.Panel
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);

        var licence = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(10, 18, 17),
            ForeColor = UiTheme.Text,
            Font = new Font("Consolas", 9),
            Text = ReadLicence(),
            Margin = new Padding(22)
        };

        var accept = new CheckBox
        {
            AutoSize = true,
            Text = "I have read and accept the Kiloview PC Onboarding Utility EULA.",
            ForeColor = UiTheme.Text,
            Padding = new Padding(0, 5, 0, 0)
        };
        var continueButton = UiTheme.Button("Accept and continue", true);
        continueButton.Enabled = false;
        continueButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        accept.CheckedChanged += (_, _) => continueButton.Enabled = accept.Checked;
        var cancel = UiTheme.Button("Cancel");
        cancel.Click += (_, _) => Close();

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(22, 14, 22, 12),
            ColumnCount = 3,
            BackColor = UiTheme.Panel
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        accept.Anchor = AnchorStyles.Left;
        actions.Controls.Add(accept, 0, 0);
        actions.Controls.Add(cancel, 1, 0);
        actions.Controls.Add(continueButton, 2, 0);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22) };
        body.Controls.Add(licence);
        Controls.Add(body);
        Controls.Add(actions);
        Controls.Add(header);
        AcceptButton = continueButton;
        CancelButton = cancel;
    }

    private static string ReadLicence()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("KiloviewPcOnboarding.License.md")
            ?? throw new InvalidOperationException("The EULA is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
