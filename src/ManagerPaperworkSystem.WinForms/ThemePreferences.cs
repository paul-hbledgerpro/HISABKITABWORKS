using System.Runtime.CompilerServices;
using Microsoft.Win32;
namespace ManagerPaperworkSystem.WinForms;

internal static class ThemePreferences
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hisab Kitab", "appearance.txt");
    private static readonly ConditionalWeakTable<Control, OriginalColors> Originals = new();
    private static readonly ConditionalWeakTable<DataGridViewCellStyle, StyleColors> Styles = new();
    public static IReadOnlyList<string> Choices { get; } = Array.AsReadOnly(new[] { "Light", "Dark", "Follow Windows" }.Concat(ThemePalette.Named.Select(x => x.Name)).ToArray());
    public static string Choice { get; private set; } = ReadChoice(PathName);
    internal static ThemePalette? Palette => ThemePalette.Named.FirstOrDefault(x => x.Name == Choice);
    public static bool Dark => Palette?.IsDark ?? (Choice == "Dark" || Choice == "Follow Windows" && WindowsIsDark());
    public static string DisplayName(string choice) => choice == "Follow Windows" ? "Same as Windows" : choice;
    private static string Normalize(string value) => value == "Same as Windows" ? "Follow Windows" : value;

    internal static string ReadChoice(string path)
    {
        try { var value = Normalize(File.ReadAllText(path).Trim()); return Choices.Contains(value) ? value : "Light"; }
        catch { return "Light"; }
    }
    internal static void SaveChoice(string path, string value)
    {
        value = Normalize(value);
        if (!Choices.Contains(value)) throw new ArgumentException("Unknown theme.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try { File.WriteAllText(temporary, value); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool WindowsIsDark()
    {
        try { return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1)) == 0; }
        catch { return false; }
    }
    public static void Set(string value)
    {
        value = Normalize(value); SaveChoice(PathName, value); Choice = value;
        foreach (Form form in Application.OpenForms) ApplyTree(form);
    }
    public static ToolStripMenuItem CreateMenu(Action<string> select)
    {
        var menu = new ToolStripMenuItem("Appearance");
        foreach (var choice in Choices)
        {
            var item = new ToolStripMenuItem(DisplayName(choice).Replace("&", "&&")) { Tag = choice, Checked = Choice == choice, CheckOnClick = false };
            item.Click += (_, _) => select(choice); menu.DropDownItems.Add(item);
        }
        menu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in menu.DropDownItems) item.Checked = (string?)item.Tag == Choice;
        };
        return menu;
    }
    public static void Attach(Form form)
    {
        form.Shown += (_, _) => ApplyTree(form);
        form.Activated += (_, _) => { if (Choice == "Follow Windows") ApplyTree(form); };
    }
    public static void ApplyTree(Control control)
    {
        // Capture inherited colors before a parent changes them. Keep business-assigned
        // colors, but do not treat our last applied palette as a new original palette.
        Capture(control);
        Apply(control, Palette, Dark);
    }
    private static void Capture(Control control)
    {
        var original = Originals.GetValue(control, c => new OriginalColors(c));
        if (original.Applied && control.BackColor != original.LastBack) original.Back = control.BackColor;
        if (original.Applied && control.ForeColor != original.LastFore) original.Fore = control.ForeColor;
        foreach (Control child in control.Controls) Capture(child);
    }
    private static Color EffectiveOriginalBackground(Control control, OriginalColors original)
    {
        if (original.Back.A != 0) return original.Back;
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (Originals.TryGetValue(parent, out var colors) && colors.Back.A != 0) return colors.Back;
        return Color.White;
    }
    private static void Apply(Control control, ThemePalette? palette, bool dark)
    {
        var original = Originals.GetValue(control, c => new OriginalColors(c));
        var originalBack = EffectiveOriginalBackground(control, original);
        control.BackColor = MapBackground(original.Back, palette, dark);
        if (palette is not null && original.Back.A != 0)
        {
            if (control is TextBoxBase or ComboBox or NumericUpDown or DateTimePicker) control.BackColor = palette.Input;
            else if (control is Form or UserControl && original.Back.GetBrightness() > .68f) control.BackColor = palette.Background;
        }
        control.ForeColor = palette is null ? (dark ? DarkForeground(original.Fore) : original.Fore)
            : IsAccent(originalBack) ? palette.OnAccent : IsNavigation(originalBack) ? Color.White : NamedInk(original.Fore, palette);
        original.LastBack = control.BackColor; original.LastFore = control.ForeColor; original.Applied = true;
        if (control is Button button)
        {
            if (palette is not null)
            {
                var colored = IsAccent(originalBack) || IsNavigation(originalBack);
                button.FlatAppearance.BorderColor = colored ? control.BackColor : palette.Border;
                button.FlatAppearance.MouseOverBackColor = colored ? Blend(control.BackColor, control.ForeColor, .12f) : palette.Alternate;
                button.FlatAppearance.MouseDownBackColor = colored ? Blend(control.BackColor, control.ForeColor, .20f) : palette.Selection;
            }
            else
            {
                button.FlatAppearance.BorderColor = original.ButtonBorder;
                button.FlatAppearance.MouseOverBackColor = dark && original.Back.GetBrightness() > .68f ? Color.FromArgb(55, 61, 70) : original.ButtonHover;
                button.FlatAppearance.MouseDownBackColor = dark && original.Back.GetBrightness() > .68f ? Color.FromArgb(65, 72, 82) : original.ButtonDown;
            }
        }
        if (control is DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = palette is not null || dark ? false : original.HeadersVisual;
            grid.BackgroundColor = palette?.Surface ?? (dark ? Color.FromArgb(28, 30, 34) : original.GridBackground);
            grid.GridColor = palette?.Border ?? original.GridLines;
            ApplyStyle(grid.DefaultCellStyle, palette, dark, 0);
            ApplyStyle(grid.AlternatingRowsDefaultCellStyle, palette, dark, 1);
            ApplyStyle(grid.ColumnHeadersDefaultCellStyle, palette, dark, 2);
            ApplyStyle(grid.RowHeadersDefaultCellStyle, palette, dark, 2);
            foreach (DataGridViewColumn column in grid.Columns) ApplyStyle(column.DefaultCellStyle, palette, dark, 3);
        }
        if (control is ToolStrip strip)
        {
            strip.Renderer = palette is null ? original.StripRenderer! : new PaletteMenuRenderer(palette);
            foreach (ToolStripItem item in strip.Items) ApplyMenu(item, palette, dark, strip is not ToolStripDropDown);
        }
        if (control.ContextMenuStrip is { } context) ApplyTree(context);
        foreach (Control child in control.Controls) Apply(child, palette, dark);
        control.Invalidate();
    }
    private static void ApplyMenu(ToolStripItem item, ThemePalette? palette, bool dark, bool topLevel)
    {
        item.BackColor = topLevel ? palette?.Navigation ?? WinTheme.BlueDark : palette?.Surface ?? (dark ? Color.FromArgb(35, 38, 44) : Color.White);
        item.ForeColor = topLevel ? Color.White : palette?.Text ?? (dark ? Color.Gainsboro : Color.Black);
        if (item is not ToolStripMenuItem menu) return;
        menu.DropDown.BackColor = palette?.Surface ?? (dark ? Color.FromArgb(35, 38, 44) : Color.White);
        if (palette is not null) menu.DropDown.Renderer = new PaletteMenuRenderer(palette);
        else menu.DropDown.RenderMode = ToolStripRenderMode.Professional;
        foreach (ToolStripItem child in menu.DropDownItems) ApplyMenu(child, palette, dark, false);
    }
    private static void ApplyStyle(DataGridViewCellStyle style, ThemePalette? palette, bool dark, int role)
    {
        var original = Styles.GetValue(style, s => new StyleColors(s));
        original.CaptureChanges(style);
        if (palette is not null)
        {
            style.BackColor = role == 3 && original.Back.IsEmpty ? Color.Empty : role == 2 ? palette.Header : role == 1 ? palette.Alternate : palette.Surface;
            style.ForeColor = role == 3 && original.Fore.IsEmpty ? Color.Empty : role == 2 ? palette.OnHeader : NamedInk(original.Fore, palette);
            style.SelectionBackColor = palette.Selection; style.SelectionForeColor = palette.OnSelection;
        }
        else if (dark && role != 3)
        {
            style.BackColor = role == 2 ? Color.FromArgb(45, 49, 56) : role == 1 ? Color.FromArgb(40, 44, 50) : Color.FromArgb(32, 35, 40);
            style.ForeColor = role == 2 ? Color.White : Color.Gainsboro;
            style.SelectionBackColor = original.SelectedBack; style.SelectionForeColor = original.SelectedFore;
        }
        else
        {
            style.BackColor = original.Back; style.ForeColor = original.Fore;
            style.SelectionBackColor = original.SelectedBack; style.SelectionForeColor = original.SelectedFore;
        }
        original.Remember(style);
    }
    public static Color Surface(Color color) => MapBackground(color, Palette, Dark);
    public static Color Ink(Color color) => Palette is { } palette ? NamedInk(color, palette) : Dark ? DarkForeground(color) : color;
    public static Color Border => Palette?.Border ?? WinTheme.Border;
    public static Color Navigation => Palette?.Navigation ?? WinTheme.BlueDark;
    public static Color NavigationEnd => Palette is { } palette ? Blend(palette.Navigation, palette.Header, .35f) : WinTheme.Blue;
    private static Color MapBackground(Color color, ThemePalette? palette, bool dark)
    {
        if (color.A == 0) return color;
        if (palette is null) return dark && color.GetBrightness() > .68f ? Color.FromArgb(30, 33, 38) : color;
        if (IsAccent(color)) return palette.Accent;
        if (IsNavigation(color)) return palette.Navigation;
        if (color.ToArgb() == WinTheme.Border.ToArgb()) return palette.Border;
        return color.GetBrightness() > .97f ? palette.Surface : palette.Alternate;
    }
    private static Color NamedInk(Color color, ThemePalette palette)
    {
        if (color.ToArgb() == WinTheme.Green.ToArgb() || color.GetHue() is > 90 and < 165 && color.GetSaturation() > .3f) return palette.Positive;
        if (color.ToArgb() == WinTheme.Red.ToArgb() || (color.GetHue() < 12 || color.GetHue() > 345) && color.GetSaturation() > .4f) return palette.Negative;
        if (color.ToArgb() == WinTheme.Muted.ToArgb() || color.ToArgb() == Color.Gray.ToArgb()) return palette.Muted;
        if (IsAccent(color)) return palette.IsDark ? palette.Accent : palette.Text;
        return palette.Text;
    }
    private static bool IsAccent(Color color) => color.A != 0 && color.GetHue() is > 15 and < 45 && color.GetSaturation() > .5f;
    private static bool IsNavigation(Color color) => color.A != 0 && color.GetHue() is > 180 and < 250 && color.GetSaturation() > .25f && color.GetBrightness() < .6f;
    private static Color DarkForeground(Color color) => color.ToArgb() == WinTheme.Green.ToArgb() ? Color.FromArgb(94, 215, 135) : color.ToArgb() == WinTheme.Red.ToArgb() ? Color.FromArgb(255, 115, 115) : color.GetBrightness() < .62f ? Color.Gainsboro : color;
    private static Color Blend(Color first, Color second, float mix) => Color.FromArgb((int)(first.R + (second.R - first.R) * mix), (int)(first.G + (second.G - first.G) * mix), (int)(first.B + (second.B - first.B) * mix));

    private sealed class OriginalColors(Control control)
    {
        public bool Applied, HeadersVisual = (control as DataGridView)?.EnableHeadersVisualStyles ?? false;
        public Color LastBack, LastFore;
        public Color Back = control.BackColor, Fore = control.ForeColor;
        public Color GridBackground = (control as DataGridView)?.BackgroundColor ?? Color.White, GridLines = (control as DataGridView)?.GridColor ?? WinTheme.Border;
        public Color ButtonBorder = (control as Button)?.FlatAppearance.BorderColor ?? Color.Empty, ButtonHover = (control as Button)?.FlatAppearance.MouseOverBackColor ?? Color.Empty, ButtonDown = (control as Button)?.FlatAppearance.MouseDownBackColor ?? Color.Empty;
        public ToolStripRenderer? StripRenderer = (control as ToolStrip)?.Renderer;
    }
    private sealed class StyleColors(DataGridViewCellStyle style)
    {
        public Color Back = style.BackColor, Fore = style.ForeColor, SelectedBack = style.SelectionBackColor, SelectedFore = style.SelectionForeColor;
        private Color _lastBack, _lastFore, _lastSelectedBack, _lastSelectedFore;
        private bool _applied;
        public void CaptureChanges(DataGridViewCellStyle current)
        {
            if (!_applied) return;
            if (current.BackColor != _lastBack) Back = current.BackColor;
            if (current.ForeColor != _lastFore) Fore = current.ForeColor;
            if (current.SelectionBackColor != _lastSelectedBack) SelectedBack = current.SelectionBackColor;
            if (current.SelectionForeColor != _lastSelectedFore) SelectedFore = current.SelectionForeColor;
        }
        public void Remember(DataGridViewCellStyle current)
        {
            _applied = true; _lastBack = current.BackColor; _lastFore = current.ForeColor; _lastSelectedBack = current.SelectionBackColor; _lastSelectedFore = current.SelectionForeColor;
        }
    }
    private sealed class PaletteMenuRenderer(ThemePalette palette) : ToolStripProfessionalRenderer(new PaletteMenuColors(palette))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Pressed) e.TextColor = Color.White;
            else if (e.Item.Selected) e.TextColor = palette.OnSelection;
            base.OnRenderItemText(e);
        }
    }
    private sealed class PaletteMenuColors(ThemePalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Surface;
        public override Color ImageMarginGradientBegin => palette.Surface;
        public override Color ImageMarginGradientMiddle => palette.Surface;
        public override Color ImageMarginGradientEnd => palette.Surface;
        public override Color MenuItemSelected => palette.Selection;
        public override Color MenuItemSelectedGradientBegin => palette.Selection;
        public override Color MenuItemSelectedGradientEnd => palette.Selection;
        public override Color MenuItemPressedGradientBegin => palette.Navigation;
        public override Color MenuItemPressedGradientMiddle => palette.Navigation;
        public override Color MenuItemPressedGradientEnd => palette.Navigation;
        public override Color MenuBorder => palette.Border;
        public override Color MenuItemBorder => palette.Border;
        public override Color CheckBackground => palette.Selection;
        public override Color CheckSelectedBackground => palette.Selection;
        public override Color CheckPressedBackground => palette.Selection;
        public override Color SeparatorDark => palette.Border;
        public override Color SeparatorLight => palette.Border;
    }
}
