using System.ComponentModel;
using System.Diagnostics;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.UI.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class InvoiceDraftReviewForm : Form
{
    public ImportedInvoice? Reviewed { get; private set; }
    public InvoiceDraftReviewForm(ImportedInvoice draft, string source)
    {
        WinTheme.Apply(this); Text = "Review Invoice Before Saving"; ClientSize = new Size(1000, 620); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); Controls.Add(root);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Check these details against the original PDF. Correct missing values and line items below.\nTax, shipping or discounts can make the invoice total differ from the product lines; confirm those amounts in the PDF." }, 0, 0);
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var vendor = new TextBox { Text = draft.VendorName, Width = 250, MaxLength = 200 };
        var number = new TextBox { Text = draft.InvoiceNumber, Width = 150, MaxLength = 100 };
        var date = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = (draft.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue), Width = 150 };
        var total = new NumericUpDown { DecimalPlaces = 2, Maximum = 99999999, Value = Math.Clamp(draft.Total ?? 0, 0, 99999999), Width = 150 };
        foreach (var (label, field) in new (string, Control)[] { ("Supplier", vendor), ("Invoice number", number), ("Invoice date", date), ("Invoice total", total) })
        { var panel = new Panel { Width = field.Width + 8, Height = 75 }; panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 24 }); field.Top = 28; panel.Controls.Add(field); fields.Controls.Add(panel); }
        root.Controls.Add(fields, 0, 1);
        var lines = new BindingList<PurchaseInvoiceLine>(draft.Lines.ToList());
        var grid = WinTheme.Grid(); grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; grid.ScrollBars = ScrollBars.Both; grid.ReadOnly = false; grid.AllowUserToAddRows = true; grid.AllowUserToDeleteRows = true; grid.AutoGenerateColumns = false;
        foreach (var name in new[] { "ItemCode", "ProductName", "Quantity", "UnitCost", "Amount" }) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, DataPropertyName = name, HeaderText = name, Width = name == "ProductName" ? 360 : 120 });
        grid.DataError += (_, e) => { e.ThrowException = false; MessageBox.Show(this, "Enter a valid number for quantity or cost.", "Invoice Review"); };
        grid.DataSource = lines; root.Controls.Add(grid, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill }; root.Controls.Add(actions, 0, 3);
        var open = WinTheme.Button("Open Original PDF"); open.AutoSize = true; open.Click += (_, _) => { if (File.Exists(source)) Process.Start(new ProcessStartInfo(source) { UseShellExecute = true }); };
        var save = WinTheme.Button("Confirm Invoice Details", true); save.AutoSize = true;
        var cancel = WinTheme.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([open, save, cancel]); CancelButton = cancel;
        save.Click += (_, _) =>
        {
            if (!grid.EndEdit()) return; (BindingContext?[lines] as CurrencyManager)?.EndCurrentEdit();
            var productLines = lines.Where(x => !string.IsNullOrWhiteSpace(x.ProductName)).ToList();
            if (string.IsNullOrWhiteSpace(vendor.Text) || string.IsNullOrWhiteSpace(number.Text) || total.Value <= 0 || productLines.Count == 0
                || productLines.Any(x => x.Quantity <= 0 || x.UnitCost <= 0))
            { MessageBox.Show(this, "Enter the supplier, invoice number, positive total, and product lines with quantity and cost."); return; }
            Reviewed = new ImportedInvoice { VendorName = vendor.Text.Trim(), InvoiceNumber = number.Text.Trim(), InvoiceDate = DateOnly.FromDateTime(date.Value), Total = total.Value, Lines = productLines };
            DialogResult = DialogResult.OK;
        };
    }
}
