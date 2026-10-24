namespace ManagerPaperworkSystem.WinForms;

/// <summary>Rearranges the existing live grid/actions, preserving their data and handlers.</summary>
internal sealed class LedgerWorkspaceHost : UserControl
{
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly Control _grid;
    private readonly Control _entry;
    public LedgerEntryPanel Entry { get; }
    public string Section { get; }
    public LedgerWorkspaceHost(string section, FlowLayoutPanel actions, Control grid, Control footer, LedgerEntryPanel entry, Control? stats = null, Control? details = null, Control? header = null, Control? gridToolbar = null)
    {
        Section = section; _grid = grid; Entry = entry; _entry = entry;
        Dock = DockStyle.Fill;
        if (grid is DataGridView ledger) LedgerGridPresentation.Attach(ledger);
        if (gridToolbar is not null)
        {
            var records = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
            records.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            records.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            records.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            gridToolbar.Dock = DockStyle.Fill; gridToolbar.Margin = Padding.Empty;
            records.Controls.Add(gridToolbar, 0, 0); records.Controls.Add(grid, 0, 1);
            _grid = records;
        }
        LedgerGridPresentation.CompactActions(actions);
        if (details is not null)
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var record = new TabPage("Record Drop"); record.Controls.Add(entry); tabs.TabPages.Add(record);
            var selected = new TabPage("Selected Shift Details"); selected.Controls.Add(details); tabs.TabPages.Add(selected); _entry = tabs;
        }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = WinTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, header is null ? 0 : 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, stats is null ? 0 : 88));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        actions.Parent?.Controls.Remove(actions); footer.Parent?.Controls.Remove(footer);
        if (header is not null) root.Controls.Add(header, 0, 0);
        root.Controls.Add(actions, 0, 1);
        if (stats is not null) { stats.Parent?.Controls.Remove(stats); if (stats is ScrollableControl scroll) scroll.AutoScroll = true; root.Controls.Add(stats, 0, 2); }
        root.Controls.Add(_body, 0, 3); root.Controls.Add(footer, 0, 4); Controls.Add(root);
    }
    public void ApplyLayout(LedgerLayout layout)
    {
        if (Entry.IsBusy) return;
        SuspendLayout(); _grid.Parent?.Controls.Remove(_grid); _entry.Parent?.Controls.Remove(_entry);
        foreach (Control old in _body.Controls.Cast<Control>().ToArray()) old.Dispose();
        var arrangement = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        if (layout == LedgerLayout.SideEntryGrid)
        {
            arrangement.ColumnCount = 2; arrangement.RowCount = 1;
            arrangement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); arrangement.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            arrangement.Controls.Add(_grid, 0, 0); arrangement.Controls.Add(_entry, 1, 0);
        }
        else
        {
            arrangement.ColumnCount = 1; arrangement.RowCount = 2;
            arrangement.RowStyles.Add(new RowStyle(SizeType.Absolute, 285)); arrangement.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            arrangement.Controls.Add(_entry, 0, 0); arrangement.Controls.Add(_grid, 0, 1);
        }
        _grid.Dock = _entry.Dock = DockStyle.Fill; _body.Controls.Add(arrangement); Entry.ApplyLayout(layout);
        ThemePreferences.ApplyTree(this); ResumeLayout(true);
    }
    public void FocusEntry()
    {
        if (_entry is TabControl tabs) tabs.SelectedIndex = 0;
        Entry.FocusEntry();
    }
}
