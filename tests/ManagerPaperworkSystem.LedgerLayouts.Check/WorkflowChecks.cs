using System.Reflection;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.UI.Services;
using ManagerPaperworkSystem.WinForms;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

internal static class WorkflowChecks
{
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    public static void Run(string output)
    {
        var rows = new[] { new ShiftLogEntry { Date = new(2026, 10, 2), ShiftNo = "9" }, new ShiftLogEntry { Date = new(2026, 10, 2), ShiftNo = "10" }, new ShiftLogEntry { Date = new(2026, 10, 1), ShiftNo = "100" } };
        Check(string.Join(',', LedgerGridPresentation.Order(rows, false, "").Select(x => x.ShiftNo)) == "10,9,100", "Date and batch descending numeric order");
        Check(string.Join(',', LedgerGridPresentation.Order(rows, true, "").Select(x => x.ShiftNo)) == "100,9,10", "Date and batch ascending numeric order");
        Check(LedgerGridPresentation.Order(rows, false, "10").Count() == 2, "Batch filter");
        foreach (var shift in new[] { true, false })
        {
            using var form = new Form { ClientSize = new Size(1200, 760), ShowInTaskbar = false, Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, Opacity = 0 };
            var entry = new LedgerEntryPanel(shift, _ => Task.CompletedTask, () => Task.FromResult((Array.Empty<string>(), Array.Empty<string>())), () => Task.FromResult(new[] { "10", "9", "9" }));
            var grid = WinTheme.Grid();
            grid.DataSource = new[] { new { Date = DateTime.Today, Batch = "10", Employee = "Long employee name", Cash = 12345.67m, Card = 10000m, Gross = 33333m, Net = 12345m, Tax = 543.21m, Drop = 12345m, Payout = 100m, PaidTo = "Long Supplier Name", Variance = -12m } };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            foreach (var label in new[] { "Dashboard", "Record Shift Drop", "Pending Shift Drops", "Save Selected Drop", "Import Z Report", "Refresh", "Delete" }) actions.Controls.Add(WinTheme.Button(label));
            using var host = new LedgerWorkspaceHost(shift ? "Shift Cash Drop" : "Cash On Hand", actions, grid, new Label(), entry);
            host.ApplyLayout(LedgerLayout.SideEntryGrid); form.Controls.Add(host); form.Show(); form.PerformLayout(); Application.DoEvents();
            typeof(MainForm).GetMethod("ConfigureResponsiveActionBar", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [actions]);
            foreach (var width in new[] { 1200, 850 })
            {
                form.ClientSize = new Size(width, 760); form.PerformLayout(); Application.DoEvents();
                Check(grid.Columns["Employee"].Width >= 185 && grid.Columns["Cash"].Width >= 125, "Readable fixed grid widths");
                Check(grid.ScrollBars == ScrollBars.Both && grid.Columns.Cast<DataGridViewColumn>().Sum(x => x.Width) > grid.Width, "Grid overflows horizontally");
                Check(!actions.AutoScroll && actions.Controls.Cast<Control>().All(x => x.Bottom <= actions.ClientSize.Height), "All actions are visible without scrolling");
            }
            var amount = entry.Controls.Find("LedgerAmount", true).OfType<NumericUpDown>().Single();
            typeof(Control).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(amount, [EventArgs.Empty]); Application.DoEvents();
            var textBox = amount.Controls.OfType<TextBox>().Single(); Check(textBox.SelectionLength == textBox.TextLength, "Tab selects full default amount");
            textBox.SelectionLength = 0;
            typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(textBox, [new MouseEventArgs(MouseButtons.Left, 1, 3, 3, 0)]);
            Application.DoEvents(); Check(textBox.SelectionLength == textBox.TextLength, "Mouse selects full amount");
            if (shift)
            {
                entry.SelectBatch("77"); entry.RefreshBatchesAsync().GetAwaiter().GetResult();
                var batch = entry.Controls.Find("LedgerBatch", true).OfType<ComboBox>().Single();
                Check(batch.Text == "77" && batch.Items.Count == 2 && batch.Items[0]?.ToString() == "10", "Dropdown preserves typed batch and numeric source order");
            }
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, $"readable-{shift}.png"));
            form.Hide();
        }
        var backupRoot = Path.Combine(output, "retention-" + Guid.NewGuid().ToString("N")); var source = Path.Combine(output, "fixture.bak"); File.WriteAllText(source, "synthetic retention bytes");
        for (var i = 0; i < 9; i++) VerifiedBackupRetention.Keep(backupRoot, "first", source);
        VerifiedBackupRetention.Keep(backupRoot, "second", source);
        Check(Directory.GetFiles(backupRoot, "*.bak", SearchOption.AllDirectories).Length == 8, "Seven retained copies per database");

        // Synthetic image-only invoice exercises the bundled engine and model, with no client data.
        using var scan = new Bitmap(1600, 1800); using (var drawing = Graphics.FromImage(scan))
        {
            drawing.Clear(Color.White); using var font = new Font("Arial", 30);
            drawing.DrawString("TEST SUPPLIER\nInvoice # INV100\nInvoice Date: 10/01/2026\n\nProduct Quantity Unit Cost Amount\nTest Product 2 5.00 10.00\n\nTotal: $10.00", font, Brushes.Black, new RectangleF(70, 80, 1450, 1600));
        }
        using var png = new MemoryStream(); scan.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var pdf = new PdfDocumentBuilder(); pdf.AddPage(612, 792).AddPng(png.ToArray(), new PdfRectangle(0, 0, 612, 792));
        var path = Path.Combine(output, "scan-fixture.pdf"); File.WriteAllBytes(path, pdf.Build());
        var recognized = ScannedInvoiceReader.Read(path, CancellationToken.None); Check(recognized.Contains("INV100"), "Bundled OCR reads scan invoice number");
        var draft = new InvoiceImportService(ScannedInvoiceReader.Read).ImportAsync(path).GetAwaiter().GetResult();
        Check(draft.Warnings.Any(x => x.Contains("OCR")) && draft.RawText.Contains("INV100"), "OCR draft requires human review");
        var paths = new FixturePaths(Path.Combine(output, "invoice-ui-" + Guid.NewGuid().ToString("N")));
        InvoiceReviewJournal.Record(paths, "store-a", path, "Needs Review", "Confirm OCR values");
        InvoiceReviewJournal.Record(paths, "store-b", path, "Imported", "Other store");
        Check(InvoiceReviewJournal.Read(paths, "store-a").Count == 1 && InvoiceReviewJournal.Read(paths, "store-a")[0].Status == "Needs Review", "Review history is store scoped");
        var service = new InvoiceEmailSyncService(paths, new InvoiceImportService(), new PurchaseService(null!, paths));
        service.SaveSettings("store-a", new InvoiceEmailSyncSettings { Enabled = true, EmailAddress = "test@example.com" });
        var refused = false;
        try { service.SaveSettings("store-b", new InvoiceEmailSyncSettings { Enabled = true, EmailAddress = "TEST@example.com" }); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "Shared mailbox cannot auto-post to multiple stores");
        using var inbox = new InvoiceInboxForm(paths, service, "store-a", "Fixture store", 1, 1, "Owner", null, _ => Task.CompletedTask);
        using var editor = new InvoiceDraftReviewForm(new ImportedInvoice { VendorName = "TEST SUPPLIER", InvoiceNumber = "INV100", InvoiceDate = new(2026, 10, 1), Total = 10,
            Lines = [new PurchaseInvoiceLine { ProductName = "Test product", Quantity = 2, UnitCost = 5, Amount = 10 }] }, path);
        foreach (var (window, file) in new[] { ((Form)inbox, "invoice-inbox"), ((Form)editor, "invoice-review") })
        {
            window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-32000, -32000); window.Opacity = 0;
            window.Show(); window.PerformLayout(); Application.DoEvents();
            using var capture = new Bitmap(window.Width, window.Height); window.DrawToBitmap(capture, new Rectangle(Point.Empty, capture.Size)); capture.Save(Path.Combine(output, file + ".png")); window.Hide();
        }
        Console.WriteLine("PASS: grid widths/scroll, visible actions at two widths, Tab/click amount selection, numeric batch dropdown/sort, local backup retention and real OCR scan.");
    }
    private sealed record FixturePaths(string AppDataDirectory) : ManagerPaperworkSystem.Core.Services.IAppPaths
    {
        public string DatabasePath => Path.Combine(AppDataDirectory, "test.db");
        public string BackupsDirectory => Path.Combine(AppDataDirectory, "backups");
    }
}
