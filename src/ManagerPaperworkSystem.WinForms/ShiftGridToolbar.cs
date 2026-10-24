namespace ManagerPaperworkSystem.WinForms;

/// <summary>Search and view controls travel with the shift grid in every ledger layout.</summary>
internal sealed class ShiftGridToolbar : UserControl
{
    private readonly TableLayoutPanel _row = new() { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
    private readonly Panel _titleSlot = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label _title = new() { Text = "Shift history", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = WinTheme.BoldFont(10), ForeColor = WinTheme.Text };
    private readonly TextBox _search = WinTheme.TextBox();
    private readonly Button _filter = IconButton("\uE71C", "Newest first");
    private readonly Button _health = IconButton("\uE9D9", "Sync health");
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tips = new();
    private readonly ToolStripMenuItem _newest = new("Newest business date first");
    private readonly ToolStripMenuItem _oldest = new("Oldest business date first");
    private readonly ToolStripMenuItem _batchHigh = new("Highest batch number first");
    private readonly ToolStripMenuItem _batchLow = new("Lowest batch number first");
    private Button? _developer;
    public bool OldestFirst { get; private set; }
    public bool ByBatch { get; private set; }
    public string SearchText => _search.Text;
    public event EventHandler? ViewChanged;
    public event EventHandler? SyncHealthRequested;

    public ShiftGridToolbar()
    {
        Name = "ShiftGridToolbar"; BackColor = WinTheme.Panel2; Padding = new Padding(10, 8, 10, 8);
        _row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        _row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        _row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        _row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _titleSlot.Controls.Add(_title); _row.Controls.Add(_titleSlot, 0, 0);
        var searchSurface = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(1), BackColor = Color.White };
        searchSurface.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        searchSurface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchSurface.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
        searchSurface.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        searchSurface.Paint += (_, e) => { using var pen = new Pen(ThemePreferences.Border); e.Graphics.DrawRectangle(pen, 0, 0, searchSurface.Width - 1, searchSurface.Height - 1); };
        var searchIcon = new Label { Text = "\uE721", Font = WinTheme.IconFont(12), ForeColor = WinTheme.Muted, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter };
        searchIcon.Click += (_, _) => _search.Focus();
        _search.Name = "ShiftBatchSearch"; _search.PlaceholderText = "Search batch"; _search.AccessibleName = "Search shift or batch number";
        _search.BorderStyle = BorderStyle.None; _search.Font = WinTheme.BodyFont(10);
        _search.Anchor = AnchorStyles.Left | AnchorStyles.Right; _search.Margin = new Padding(0, 0, 2, 0);
        _search.TextChanged += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _search.Clear(); e.SuppressKeyPress = true; } };
        var clear = IconButton("\uE711", ""); clear.Name = "ClearBatchSearch"; clear.AccessibleName = "Clear batch search";
        clear.FlatAppearance.BorderSize = 0; clear.Margin = new Padding(1); clear.TabStop = false;
        clear.Click += (_, _) => { _search.Clear(); _search.Focus(); }; _tips.SetToolTip(clear, "Clear search (Esc)");
        searchSurface.Controls.Add(searchIcon, 0, 0); searchSurface.Controls.Add(_search, 1, 0); searchSurface.Controls.Add(clear, 2, 0);
        _row.Controls.Add(searchSurface, 1, 0);
        _filter.Name = "ShiftViewFilter"; _filter.AccessibleName = "Filter and sort shift history";
        _filter.Margin = new Padding(0, 0, 8, 0); _row.Controls.Add(_filter, 2, 0);
        _health.Name = "ShiftSyncHealth"; _health.Margin = Padding.Empty; _row.Controls.Add(_health, 3, 0);
        _health.Click += (_, _) => SyncHealthRequested?.Invoke(this, EventArgs.Empty);
        _newest.Click += (_, _) => SetSort(false, false); _oldest.Click += (_, _) => SetSort(true, false);
        _batchHigh.Click += (_, _) => SetSort(false, true); _batchLow.Click += (_, _) => SetSort(true, true);
        var reset = new ToolStripMenuItem("Reset view"); reset.Click += (_, _) => { _search.Clear(); SetSort(false, false); };
        _menu.Items.AddRange([_newest, _oldest, _batchHigh, _batchLow, new ToolStripSeparator(), reset]);
        _menu.Opening += (_, _) =>
        {
            _newest.Checked = !ByBatch && !OldestFirst; _oldest.Checked = !ByBatch && OldestFirst;
            _batchHigh.Checked = ByBatch && !OldestFirst; _batchLow.Checked = ByBatch && OldestFirst;
            ThemePreferences.ApplyTree(_menu);
        };
        _filter.Click += (_, _) => _menu.Show(_filter, new Point(0, _filter.Height));
        _tips.SetToolTip(_filter, "Sort by business date or batch number"); _tips.SetToolTip(_health, "View synchronization results");
        Controls.Add(_row); SizeChanged += (_, _) => FitTitle();
    }

    public void SetSort(bool oldestFirst, bool byBatch)
    {
        OldestFirst = oldestFirst; ByBatch = byBatch;
        _filter.Text = byBatch ? (oldestFirst ? "Batch: low first" : "Batch: high first") : (oldestFirst ? "Oldest first" : "Newest first");
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDeveloperSetup(Button button)
    {
        _developer = button; button.Dock = DockStyle.Fill; button.Margin = Padding.Empty;
        button.Font = WinTheme.BoldFont(9); button.Padding = Padding.Empty;
        button.AccessibleName = "Developer Z Report Auto Sync Setup";
        _tips.SetToolTip(button, "Developer Z Report Auto Sync Setup");
        _titleSlot.Controls.Add(button); button.BringToFront(); FitTitle();
    }

    private void FitTitle()
    {
        var compact = ClientSize.Width < 680;
        _row.ColumnStyles[0].Width = compact ? 38 : 148;
        _title.Visible = !compact;
        if (_developer is not null) { _developer.Text = compact ? "\uE713" : "Z Auto Sync Setup"; _developer.Font = compact ? WinTheme.IconFont(12) : WinTheme.BoldFont(9); }
    }

    private static Button IconButton(string glyph, string text)
    {
        var button = WinTheme.Button(text); button.Dock = DockStyle.Fill; button.AutoSize = false;
        button.Font = WinTheme.BodyFont(9); button.Padding = new Padding(text.Length == 0 ? 0 : 23, 0, 2, 0);
        button.Paint += (_, e) =>
        {
            using var font = WinTheme.IconFont(11);
            var bounds = text.Length == 0 ? button.ClientRectangle : new Rectangle(4, 0, 24, button.Height);
            TextRenderer.DrawText(e.Graphics, glyph, font, bounds, button.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        };
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _menu.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
