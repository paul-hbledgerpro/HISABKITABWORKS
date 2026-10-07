using System.Runtime.CompilerServices;
using Microsoft.Win32;
namespace ManagerPaperworkSystem.WinForms;

internal static class ThemePreferences
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hisab Kitab", "appearance.txt");
    private static readonly ConditionalWeakTable<Control, OriginalColors> Originals = new();
    public static string Choice { get; private set; } = Load();
    private static string Load() { try { var value = File.ReadAllText(PathName).Trim(); return value is "Dark" or "Follow Windows" ? value : "Light"; } catch { return "Light"; } }
    public static bool Dark { get { if (Choice == "Dark") return true; if (Choice != "Follow Windows") return false; try { return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1)) == 0; } catch { return false; } } }
    public static void Set(string value)
    {
        if (value is not ("Light" or "Dark" or "Follow Windows")) throw new ArgumentException("Unknown theme.");
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); var temporary = PathName + ".new"; File.WriteAllText(temporary, value); File.Move(temporary, PathName, true); Choice = value;
        foreach (Form form in Application.OpenForms) ApplyTree(form);
    }
    public static void Attach(Form form)
    {
        form.Shown += (_, _) => ApplyTree(form);
        form.Activated += (_, _) => { if (Choice == "Follow Windows") ApplyTree(form); };
    }
    public static void ApplyTree(Control control)
    {
        var original = Originals.GetValue(control, c => new OriginalColors(c));
        if (original.Applied && control.BackColor != original.LastBack) original.Back = control.BackColor;
        if (original.Applied && control.ForeColor != original.LastFore) original.Fore = control.ForeColor;
        control.BackColor = Dark ? Background(original.Back) : original.Back;
        control.ForeColor = Dark ? Foreground(original.Fore) : original.Fore;
        original.LastBack = control.BackColor; original.LastFore = control.ForeColor; original.Applied = true;
        if (control is Button button && original.Back.GetBrightness() > .68f)
        {
            button.FlatAppearance.MouseOverBackColor = Dark ? Color.FromArgb(55, 61, 70) : Color.FromArgb(244, 248, 252);
            button.FlatAppearance.MouseDownBackColor = Dark ? Color.FromArgb(65, 72, 82) : Color.FromArgb(225, 235, 245);
        }
        if (control is DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = Dark ? false : original.HeadersVisual;
            grid.BackgroundColor = Dark ? Color.FromArgb(28, 30, 34) : original.GridBackground;
            grid.DefaultCellStyle = original.Cells!.Clone(); grid.AlternatingRowsDefaultCellStyle = original.Alternate!.Clone(); grid.ColumnHeadersDefaultCellStyle = original.Header!.Clone();
            if (Dark) { grid.EnableHeadersVisualStyles = false; grid.DefaultCellStyle.BackColor = Color.FromArgb(32, 35, 40); grid.DefaultCellStyle.ForeColor = Color.Gainsboro; grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(40, 44, 50); grid.AlternatingRowsDefaultCellStyle.ForeColor = Color.Gainsboro; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 49, 56); grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White; }
        }
        if (control is ToolStrip strip) foreach (ToolStripItem item in strip.Items) ApplyMenu(item);
        foreach (Control child in control.Controls) ApplyTree(child);
        control.Invalidate();
    }
    private static void ApplyMenu(ToolStripItem item)
    {
        if (item is ToolStripMenuItem menu) foreach (ToolStripItem child in menu.DropDownItems) { child.BackColor = Dark ? Color.FromArgb(35, 38, 44) : Color.White; child.ForeColor = Dark ? Color.Gainsboro : Color.Black; ApplyMenu(child); }
    }
    public static Color Surface(Color color) => Dark ? Background(color) : color;
    public static Color Ink(Color color) => Dark ? Foreground(color) : color;
    private static Color Background(Color color) => color.A == 0 ? color : color.GetBrightness() > .68f ? Color.FromArgb(30, 33, 38) : color;
    private static Color Foreground(Color color) => color.ToArgb() == WinTheme.Green.ToArgb() ? Color.FromArgb(94, 215, 135) : color.ToArgb() == WinTheme.Red.ToArgb() ? Color.FromArgb(255, 115, 115) : color.GetBrightness() < .62f ? Color.Gainsboro : color;
    private sealed class OriginalColors(Control control)
    {
        public bool Applied, HeadersVisual = (control as DataGridView)?.EnableHeadersVisualStyles ?? false;
        public Color LastBack, LastFore;
        public Color Back = control.BackColor, Fore = control.ForeColor, GridBackground = control is DataGridView g ? g.BackgroundColor : Color.White;
        public DataGridViewCellStyle? Cells = (control as DataGridView)?.DefaultCellStyle.Clone(), Alternate = (control as DataGridView)?.AlternatingRowsDefaultCellStyle.Clone(), Header = (control as DataGridView)?.ColumnHeadersDefaultCellStyle.Clone();
    }
}
