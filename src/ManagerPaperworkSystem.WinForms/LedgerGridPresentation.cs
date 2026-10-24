using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.WinForms;

internal static class LedgerGridPresentation
{
    public static long BatchNumber(string? value) => long.TryParse(value, out var number) ? number : long.MinValue;
    public static IEnumerable<ShiftLogEntry> Order(IEnumerable<ShiftLogEntry> rows, bool oldest, string search, bool batchOnly = false)
    {
        rows = rows.Where(x => string.IsNullOrWhiteSpace(search) || x.ShiftNo.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        if (batchOnly) return oldest ? rows.OrderBy(x => BatchNumber(x.ShiftNo)).ThenBy(x => x.Date).ThenBy(x => x.Id)
            : rows.OrderByDescending(x => BatchNumber(x.ShiftNo)).ThenByDescending(x => x.Date).ThenByDescending(x => x.Id);
        return oldest ? rows.OrderBy(x => x.Date).ThenBy(x => BatchNumber(x.ShiftNo)).ThenBy(x => x.Id)
            : rows.OrderByDescending(x => x.Date).ThenByDescending(x => BatchNumber(x.ShiftNo)).ThenByDescending(x => x.Id);
    }
    public static void Attach(DataGridView grid)
    {
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ScrollBars = ScrollBars.Both;
        grid.AllowUserToResizeColumns = true;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.DataBindingComplete += (_, _) => SizeColumns(grid);
        SizeColumns(grid);
    }
    private static void SizeColumns(DataGridView grid)
    {
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.Name is "Date" or "Batch") col.SortMode = DataGridViewColumnSortMode.Programmatic;
            col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            var width = col.Name switch
            {
                "Date" => 120, "Batch" => 100, "Employee" or "PaidTo" or "Vendor" or "EnteredBy" => 185,
                "Source" or "Status" => 185, "PayoutReason" or "Purpose" or "Description" or "CorrectionReason" => 250,
                "EnteredUtc" => 190, _ => 125
            };
            col.MinimumWidth = width;
            if (col.Name == "Date") { col.HeaderText = "Business Date"; col.DefaultCellStyle.Format = "M/d/yyyy"; }
            if (col.Name == "PaidTo") col.HeaderText = "Paid To / Vendor";
            if (col.Name == "EnteredUtc") col.HeaderText = "Entered / Imported UTC";
            if (col.Name is "Cash" or "Card" or "Gross" or "Net" or "Tax" or "Drop" or "Payout" or "Variance" or "CashAdded")
            { col.DefaultCellStyle.Format = "N2"; col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; }
            col.Width = Math.Max(width, col.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true) + 8);
        }
    }
    public static void CompactActions(FlowLayoutPanel actions)
    {
        actions.Tag = "LedgerActions"; actions.AutoScroll = false; actions.WrapContents = true;
        actions.Padding = new Padding(0, 3, 0, 3);
        foreach (Button button in actions.Controls.OfType<Button>())
        {
            button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(0, 36); button.Height = 36;
            button.Font = WinTheme.BoldFont(9); button.Padding = new Padding(5, 0, 5, 0); button.Margin = new Padding(2);
        }
    }
}
