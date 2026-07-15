using System.Drawing.Drawing2D;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal static class AdminTheme
{
    public static readonly Color Bg = Color.FromArgb(245, 248, 252);
    public static readonly Color Panel = Color.White;
    public static readonly Color Panel2 = Color.FromArgb(222, 232, 245);
    public static readonly Color Copper = Color.FromArgb(242, 125, 30);
    public static readonly Color CopperDark = Color.FromArgb(196, 82, 0);
    public static readonly Color Blue = Color.FromArgb(27, 83, 151);
    public static readonly Color BlueDark = Color.FromArgb(13, 48, 88);
    public static readonly Color Text = Color.FromArgb(25, 44, 68);
    public static readonly Color Muted = Color.FromArgb(91, 108, 131);
    public static readonly Color Green = Color.FromArgb(24, 132, 82);
    public static readonly Color Red = Color.FromArgb(196, 55, 55);

    public static Font Header(float size = 18) => new("Segoe UI", size, FontStyle.Bold);
    public static Font Body(float size = 10) => new("Segoe UI", size, FontStyle.Regular);
    public static Font Bold(float size = 10) => new("Segoe UI", size, FontStyle.Bold);
    public static Font Icon(float size = 15) => new("Segoe MDL2 Assets", size, FontStyle.Regular);

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Height = 44,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Copper : Panel,
            ForeColor = primary ? Color.White : Blue,
            Font = Bold(),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(4)
        };
        button.FlatAppearance.BorderColor = primary ? CopperDark : Blue;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255, 151, 58) : Color.FromArgb(235, 242, 251);
        button.FlatAppearance.MouseDownBackColor = primary ? CopperDark : Panel2;
        return button;
    }

    public static TextBox TextBox(bool password = false)
        => new()
        {
            BackColor = Color.White,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Body(10.5f),
            Height = 36,
            UseSystemPasswordChar = password,
            Margin = Padding.Empty
        };

    public static NumericUpDown NumberBox(decimal minimum = 1, decimal maximum = 999, decimal value = 1)
        => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            BackColor = Color.White,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Body(10.5f),
            ThousandsSeparator = true,
            Margin = Padding.Empty
        };

    public static Label Label(string text, Color? color = null, float size = 10, bool bold = false)
        => new()
        {
            Text = text,
            ForeColor = color ?? Text,
            Font = bold ? Bold(size) : Body(size),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };

    public static Panel Card(Color? border = null)
    {
        var panel = new Panel { BackColor = Panel, Tag = border ?? Panel2 };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(panel.Tag is Color color ? color : Panel2);
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, panel.Width - 1), Math.Max(0, panel.Height - 1));
        };
        return panel;
    }

    public static void PaintGradient(PaintEventArgs e, Rectangle bounds)
    {
        using var brush = new LinearGradientBrush(bounds, BlueDark, Blue, 0f);
        e.Graphics.FillRectangle(brush, bounds);
    }

    public static Icon? LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab.ico");
        return File.Exists(path) ? new Icon(path) : null;
    }

    public static Image? LoadLogo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab_Logo.png");
        return File.Exists(path) ? Image.FromFile(path) : null;
    }
}
