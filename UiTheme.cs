namespace KiloviewPcOnboarding;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(7, 14, 13);
    public static readonly Color Panel = Color.FromArgb(15, 25, 23);
    public static readonly Color Border = Color.FromArgb(48, 67, 62);
    public static readonly Color Text = Color.FromArgb(237, 244, 241);
    public static readonly Color Muted = Color.FromArgb(153, 174, 166);
    public static readonly Color Green = Color.FromArgb(184, 243, 74);
    public static readonly Color Amber = Color.FromArgb(242, 190, 72);
    public static readonly Color Red = Color.FromArgb(244, 108, 98);

    public static Label Label(string text, float size = 9, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Text,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    public static Button Button(string text, bool primary = false) => new Button()
    {
        Text = text,
        AutoSize = false,
        Height = 38,
        Width = 150,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Green : Panel,
        ForeColor = primary ? Background : Text,
        Font = new Font("Segoe UI", 9, FontStyle.Bold),
        Cursor = Cursors.Hand
    }.Also(button => button.FlatAppearance.BorderColor = primary ? Green : Border);

    private static T Also<T>(this T value, Action<T> update)
    {
        update(value);
        return value;
    }
}
