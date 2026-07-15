using System.Drawing.Drawing2D;

namespace ManagerPaperworkSystem.WinForms;

internal static class WinTheme
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

    public static Font HeaderFont(float size = 18) => new("Segoe UI", size, FontStyle.Bold);
    public static Font BodyFont(float size = 10) => new("Segoe UI", size, FontStyle.Regular);
    public static Font BoldFont(float size = 10) => new("Segoe UI", size, FontStyle.Bold);
    public static Font IconFont(float size = 15) => new("Segoe MDL2 Assets", size, FontStyle.Regular);

    public static void Apply(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = BodyFont();
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Icon = TryLoadIcon();
    }

    public static Icon? TryLoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab.ico");
        return File.Exists(path) ? new Icon(path) : null;
    }

    public static Image? TryLoadLogo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab_Logo.png");
        return File.Exists(path) ? Image.FromFile(path) : null;
    }

    public static Image? TryLoadLoginHero()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab_LoginHero.png");
        return File.Exists(path) ? Image.FromFile(path) : null;
    }

    public static Image? TryLoadLoginLeftArt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab_LoginLeftArt.png");
        return File.Exists(path) ? Image.FromFile(path) : null;
    }

    public static Button Button(string text, bool filled = false)
    {
        var b = new Button
        {
            Text = text,
            Height = 44,
            FlatStyle = FlatStyle.Flat,
            ForeColor = filled ? Color.Black : Copper,
            BackColor = filled ? Copper : Panel,
            Font = BoldFont(),
            Cursor = Cursors.Hand,
            Margin = new Padding(4),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(8, 0, 8, 0),
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderColor = CopperDark;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = filled ? Color.FromArgb(234, 180, 115) : Panel2;
        b.FlatAppearance.MouseDownBackColor = CopperDark;
        return b;
    }

    public static TextBox TextBox()
    {
        return new TextBox
        {
            BackColor = Panel,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = BodyFont(11),
            Height = 36,
            Margin = new Padding(4)
        };
    }

    public static ComboBox ComboBox()
    {
        return new ComboBox
        {
            BackColor = Color.White,
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.Flat,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = BodyFont(10),
            Height = 36,
            Margin = new Padding(4)
        };
    }

    public static DateTimePicker DatePicker()
    {
        return new DateTimePicker
        {
            CalendarForeColor = Color.Black,
            CalendarMonthBackground = Color.White,
            Format = DateTimePickerFormat.Short,
            Font = BodyFont(11),
            Height = 36,
            Margin = new Padding(4)
        };
    }

    public static Label Label(string text, bool accent = false)
    {
        return new Label
        {
            Text = text,
            ForeColor = accent ? Copper : Text,
            AutoSize = true,
            Font = accent ? BoldFont() : BodyFont(),
            Margin = new Padding(4, 8, 4, 2)
        };
    }

    public static Panel Card()
    {
        var panel = new Panel
        {
            BackColor = Panel,
            Padding = new Padding(12),
            Margin = new Padding(8)
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(CopperDark);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    public static Button IconButton(string glyph, string text, bool filled = false)
    {
        var button = Button($"{glyph}  {text}", filled);
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        button.TextAlign = ContentAlignment.MiddleCenter;
        return button;
    }

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            BackgroundColor = Panel,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = Color.FromArgb(57, 76, 96),
            ForeColor = Text,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            RowTemplate = { Height = 34 }
        };
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Copper;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
        grid.ColumnHeadersDefaultCellStyle.Font = BoldFont();
        grid.DefaultCellStyle.BackColor = Panel;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 76, 106);
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(8, 36, 54);
        grid.DefaultCellStyle.Font = BodyFont(10);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.DataBindingComplete += (_, _) => ApplyFriendlyHeaders(grid);
        return grid;
    }

    private static void ApplyFriendlyHeaders(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            column.HeaderText = column.HeaderText switch
            {
                "ShiftNo" => "Shift #",
                "CashTotal" => "Cash",
                "CardTotal" => "Card",
                "CashAdded" => "Cash Added",
                "IsPayout" => "Is Payout",
                "PayoutAmount" => "Payout",
                "CashDropReceived" => "Drop",
                "RegisterPayout" => "Payout",
                "PayoutReason" => "Payout Reason",
                "NetSales" => "Net Sales",
                "GrossSales" => "Gross Sales",
                "CheckAmount" => "Amount",
                "CheckNumber" => "Check #",
                "VendorName" => "Vendor",
                _ => SplitPascal(column.HeaderText)
            };
        }
    }

    private static string SplitPascal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        var result = "";
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && !char.IsWhiteSpace(text[i - 1]))
                result += " ";
            result += text[i];
        }
        return result;
    }

    public static Label FixedLabel(string text, bool accent = false, float size = 10, bool bold = false)
        => new()
        {
            Text = text,
            ForeColor = accent ? Copper : Text,
            Font = bold || accent ? BoldFont(size) : BodyFont(size),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

    public static Panel BorderedPanel(int padding = 12)
    {
        var panel = new Panel { BackColor = Panel, Padding = new Padding(padding) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(CopperDark);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    public static void PaintGradient(PaintEventArgs e, Rectangle bounds)
    {
        using var brush = new LinearGradientBrush(bounds, Color.FromArgb(7, 28, 43), Color.FromArgb(20, 55, 78), 0f);
        e.Graphics.FillRectangle(brush, bounds);
    }
}
