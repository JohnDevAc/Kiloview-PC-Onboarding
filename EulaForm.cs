using System.Reflection;

namespace KiloviewPcOnboarding;

internal sealed class EulaForm : Form
{
    public EulaForm(Icon icon)
    {
        Icon = icon;
        Text = "Kiloview PC Onboarding — End User Licence Agreement";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 640);
        Size = new Size(900, 720);
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;

        var title = UiTheme.Label("Kiloview PC Onboarding", 20, true);
        var subtitle = UiTheme.Label("Review and accept the licence before configuring this Windows PC.", 10);
        subtitle.ForeColor = UiTheme.Muted;
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 82,
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

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            Padding = new Padding(22, 14, 22, 12),
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = UiTheme.Panel
        };
        actions.Controls.Add(continueButton);
        actions.Controls.Add(cancel);
        actions.Controls.Add(accept);

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
