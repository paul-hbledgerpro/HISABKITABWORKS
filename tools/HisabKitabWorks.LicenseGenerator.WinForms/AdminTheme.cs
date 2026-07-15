using System.Drawing.Drawing2D;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal static class AdminTheme
{
    public static readonly Color Bg = Color.FromArgb(6, 30, 44);
    public static readonly Color Panel = Color.FromArgb(10, 42, 62);
    public static readonly Color Panel2 = Color.FromArgb(16, 55, 80);
    public static readonly Color Copper = Color.FromArgb(219, 157, 91);
    public static readonly Color CopperDark = Color.FromArgb(175, 103, 39);
    public static readonly Color Text = Color.White;
    public static readonly Color Muted = Color.FromArgb(170, 188, 210);
    public static readonly Color Green = Color.FromArgb(45, 211, 111);
    public static readonly Color Red = Color.FromArgb(255, 69, 58);

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
            ForeColor = primary ? Color.Black : Copper,
            Font = Bold(),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(4)
        };
        button.FlatAppearance.BorderColor = CopperDark;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(234, 180, 115) : Panel2;
        button.FlatAppearance.MouseDownBackColor = CopperDark;
        return button;
    }

    public static TextBox TextBox(bool password = false)
        => new()
        {
            BackColor = Bg,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Body(10.5f),
            Height = 36,
            UseSystemPasswordChar = password,
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
        using var brush = new LinearGradientBrush(bounds, Color.FromArgb(7, 28, 43), Color.FromArgb(20, 55, 78), 0f);
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
