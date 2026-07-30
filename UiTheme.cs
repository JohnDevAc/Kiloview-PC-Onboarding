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

    public static Button RefreshButton(string accessibleName) => new RefreshIconButton()
    {
        AccessibleName = accessibleName,
        AutoSize = false,
        Height = 22,
        Width = 22,
        Margin = Padding.Empty,
        FlatStyle = FlatStyle.Flat,
        BackColor = Panel,
        ForeColor = Text,
        Cursor = Cursors.Hand
    }.Also(button =>
    {
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 43, 39);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(37, 55, 50);
    });

    public static void ConfigureForm(Form form)
    {
        // WinForms can render point-based fonts at the monitor DPI while leaving
        // runtime-created pixel measurements at 96 DPI. Scale the geometry once
        // after the handle has acquired its real monitor DPI, while preserving
        // the font point sizes so they are not enlarged twice.
        form.AutoScaleMode = AutoScaleMode.None;
        form.Load += (_, _) => ScaleLayoutForCurrentDpi(form);
        form.Shown += (_, _) => ReclaimForeground(form);
    }

    public static void MaximizeIfNeeded(Form form)
    {
        var workingArea = Screen.FromControl(form).WorkingArea;
        if (form.Width > workingArea.Width || form.Height > workingArea.Height)
            form.WindowState = FormWindowState.Maximized;
    }

    public static void ReclaimForeground(Form form, int durationMilliseconds = 1800)
    {
        if (form.IsDisposed)
            return;

        var remainingAttempts = Math.Max(1, durationMilliseconds / 200);
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        void Activate()
        {
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
        }

        timer.Tick += (_, _) =>
        {
            if (form.IsDisposed)
            {
                timer.Dispose();
                return;
            }

            Activate();
            if (--remainingAttempts > 0)
                return;

            timer.Stop();
            form.TopMost = false;
            form.BringToFront();
            form.Activate();
            timer.Dispose();
        };
        Activate();
        timer.Start();
    }

    private static void ScaleLayoutForCurrentDpi(Form form)
    {
        var scale = form.DeviceDpi / 96f;
        if (scale <= 1.01f)
            return;

        ScaleLayout(form, scale);
    }

    private static void ScaleLayout(Form form, float scale)
    {
        var controls = DescendantsAndSelf(form).ToArray();
        var fonts = controls
            .Select(control => (Control: control, Font: (Font)control.Font.Clone()))
            .ToArray();
        var clientSize = form.ClientSize;
        var minimumSize = form.MinimumSize;
        var maximumSize = form.MaximumSize;

        foreach (var control in controls)
            control.SuspendLayout();
        try
        {
            foreach (Control child in form.Controls)
                child.Scale(new SizeF(scale, scale));

            form.ClientSize = Scale(clientSize, scale);
            form.MinimumSize = Scale(minimumSize, scale);
            if (!maximumSize.IsEmpty)
                form.MaximumSize = Scale(maximumSize, scale);

            foreach (var item in fonts)
                item.Control.Font = item.Font;
        }
        finally
        {
            foreach (var control in controls.Reverse())
                control.ResumeLayout(false);
            form.PerformLayout();
        }
    }

    private static Size Scale(Size size, float scale) => new(
        (int)Math.Round(size.Width * scale),
        (int)Math.Round(size.Height * scale));

    private static IEnumerable<Control> DescendantsAndSelf(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
        }
    }

    private static T Also<T>(this T value, Action<T> update)
    {
        update(value);
        return value;
    }
}

internal sealed class RefreshIconButton : Button
{
    public RefreshIconButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var color = Enabled ? UiTheme.Green : UiTheme.Muted;
        var diameter = Math.Min(ClientSize.Width, ClientSize.Height) * 0.66f;
        var stroke = Math.Max(2.4f, diameter * 0.16f);
        var left = (ClientSize.Width - diameter) / 2f;
        var top = (ClientSize.Height - diameter) / 2f;
        using var pen = new Pen(color, stroke)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        using var brush = new SolidBrush(color);
        var radius = diameter / 2f;
        var centre = new PointF(left + radius, top + radius);

        DrawClockwiseArrow(190, 120);
        DrawClockwiseArrow(10, 120);

        void DrawClockwiseArrow(float startAngle, float sweepAngle)
        {
            e.Graphics.DrawArc(pen, left, top, diameter, diameter, startAngle, sweepAngle);

            var endRadians = (startAngle + sweepAngle) * MathF.PI / 180f;
            var tip = new PointF(
                centre.X + MathF.Cos(endRadians) * radius,
                centre.Y + MathF.Sin(endRadians) * radius);
            var tangent = new PointF(-MathF.Sin(endRadians), MathF.Cos(endRadians));
            var normal = new PointF(-tangent.Y, tangent.X);
            var arrowLength = diameter * 0.34f;
            var arrowWidth = diameter * 0.22f;
            var back = new PointF(
                tip.X - tangent.X * arrowLength,
                tip.Y - tangent.Y * arrowLength);
            e.Graphics.FillPolygon(
                brush,
                [
                    tip,
                    new PointF(back.X + normal.X * arrowWidth, back.Y + normal.Y * arrowWidth),
                    new PointF(back.X - normal.X * arrowWidth, back.Y - normal.Y * arrowWidth)
                ]);
        }
    }
}

internal enum ActivityState
{
    Idle,
    Working,
    Complete,
    Error
}

internal sealed class ActivityIndicator : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 80 };
    private ActivityState _state;
    private float _angle;

    public ActivityIndicator()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        Size = new Size(22, 22);
        Margin = Padding.Empty;
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 24) % 360;
            Invalidate();
        };
    }

    public ActivityState State
    {
        get => _state;
        set
        {
            if (_state == value)
                return;

            _state = value;
            if (value == ActivityState.Working)
                _timer.Start();
            else
                _timer.Stop();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var inset = Math.Max(2f, DeviceDpi / 96f * 2f);
        var bounds = new RectangleF(
            inset,
            inset,
            Math.Max(1, ClientSize.Width - inset * 2),
            Math.Max(1, ClientSize.Height - inset * 2));
        var stroke = Math.Max(2f, DeviceDpi / 96f * 2.2f);

        switch (_state)
        {
            case ActivityState.Working:
                using (var pen = new Pen(UiTheme.Green, stroke)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                })
                {
                    e.Graphics.DrawArc(pen, bounds, _angle, 260);
                }
                break;

            case ActivityState.Complete:
                using (var pen = new Pen(UiTheme.Green, stroke)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                })
                {
                    e.Graphics.DrawLines(
                        pen,
                        [
                            new PointF(ClientSize.Width * 0.18f, ClientSize.Height * 0.53f),
                            new PointF(ClientSize.Width * 0.42f, ClientSize.Height * 0.76f),
                            new PointF(ClientSize.Width * 0.84f, ClientSize.Height * 0.25f)
                        ]);
                }
                break;

            case ActivityState.Error:
                using (var pen = new Pen(UiTheme.Red, stroke)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                })
                {
                    e.Graphics.DrawLine(
                        pen,
                        ClientSize.Width * 0.27f,
                        ClientSize.Height * 0.27f,
                        ClientSize.Width * 0.73f,
                        ClientSize.Height * 0.73f);
                    e.Graphics.DrawLine(
                        pen,
                        ClientSize.Width * 0.73f,
                        ClientSize.Height * 0.27f,
                        ClientSize.Width * 0.27f,
                        ClientSize.Height * 0.73f);
                }
                break;

            default:
                using (var pen = new Pen(UiTheme.Border, stroke))
                    e.Graphics.DrawEllipse(pen, bounds);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}
