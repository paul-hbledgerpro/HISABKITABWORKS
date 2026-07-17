using System.Drawing.Drawing2D;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal static class DeveloperTheme
{
    public static readonly Color Bg = Color.FromArgb(245, 248, 252);
    public static readonly Color Panel = Color.White;
    public static readonly Color PaleBlue = Color.FromArgb(224, 235, 248);
    public static readonly Color Orange = Color.FromArgb(247, 127, 25);
    public static readonly Color OrangeDark = Color.FromArgb(195, 83, 0);
    public static readonly Color Blue = Color.FromArgb(31, 91, 166);
    public static readonly Color Navy = Color.FromArgb(10, 48, 89);
    public static readonly Color Text = Color.FromArgb(24, 45, 72);
    public static readonly Color Muted = Color.FromArgb(88, 109, 137);
    public static readonly Color Green = Color.FromArgb(17, 142, 76);
    public static readonly Color Red = Color.FromArgb(205, 52, 52);

    public static Font Body(float size = 10) => new("Segoe UI", size);
    public static Font Bold(float size = 10) => new("Segoe UI", size, FontStyle.Bold);
    public static TextBox TextBox(bool password = false) => new() { Dock = DockStyle.Fill, Font = Body(10.5f), BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = password };
    public static Label Label(string text, bool bold = false, Color? color = null) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = bold ? Bold() : Body(), ForeColor = color ?? Text };
    public static Button Button(string text, bool primary = false) { var b = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Font = Bold(), BackColor = primary ? Orange : Color.White, ForeColor = primary ? Color.White : Blue, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = primary ? OrangeDark : Blue; return b; }
    public static NumericUpDown Number(decimal value = 1) => new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = 999, Value = value, Font = Body(10.5f) };
    public static void Gradient(PaintEventArgs e, Rectangle bounds) { using var brush = new LinearGradientBrush(bounds, Navy, Blue, 0f); e.Graphics.FillRectangle(brush, bounds); }
    public static Icon? Icon() { var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab.ico"); return File.Exists(path) ? new Icon(path) : null; }
    public static Image? Logo() { var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab_Logo.png"); return File.Exists(path) ? Image.FromFile(path) : null; }
}
