using System.Diagnostics;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class InvoiceInboxForm : Form
{
    private bool _busy;
    public InvoiceInboxForm(IAppPaths paths, InvoiceEmailSyncService service, string key, string storeName,
        int storeId, int userId, string userName, LicensedBusinessConnection? business, Func<string, Task> review)
    {
        WinTheme.Apply(this); Text = "Invoice Inbox — " + storeName; ClientSize = new Size(1000, 650);
        MinimumSize = new Size(800, 550); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        foreach (var height in new[] { 80, 60, 70 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); Controls.Add(root);
        var status = new Label { Dock = DockStyle.Fill, Text = "Connect Gmail to collect invoice PDFs, or use your store invoice address.\nGoogle grants read-only mailbox access; its account security requirements still apply.", AutoEllipsis = true };
        root.Controls.Add(status, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        root.Controls.Add(actions, 0, 1);
        var connect = WinTheme.Button("Connect Gmail", true); connect.AutoSize = true;
        var disconnect = WinTheme.Button("Disconnect Gmail"); disconnect.AutoSize = true;
        var sync = WinTheme.Button("Check Invoices", true); sync.AutoSize = true;
        var month = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "MMMM yyyy", ShowUpDown = true, Width = 180 };
        var older = new CheckBox { Text = "Include month", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange([connect, disconnect, sync, older, month]);
        var inboxActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        root.Controls.Add(inboxActions, 0, 2);
        var copy = WinTheme.Button("Copy Store Invoice Address"); copy.AutoSize = true;
        var forwarding = WinTheme.Button("Forwarding Help"); forwarding.AutoSize = true;
        copy.Enabled = !string.IsNullOrWhiteSpace(business?.InvoiceInboxAddress);
        inboxActions.Controls.AddRange([copy, forwarding, new Label { AutoSize = true, MaximumSize = new Size(480, 0),
            Text = business?.InvoiceInboxAddress ?? "A store invoice address has not been configured." }]);
        copy.Click += (_, _) => { Clipboard.SetText(business!.InvoiceInboxAddress); status.Text = "Copied. Give this address to suppliers for this store only."; };
        forwarding.Click += (_, _) => MessageBox.Show(this,
            "In Gmail Settings → Forwarding and POP/IMAP, add your store invoice address and complete Google's verification.\n\n" +
            "Create a Gmail filter for selected suppliers, then choose Forward it to this address. Forwarding collects future mail only. " +
            "Use Connect Gmail with Include month, or Import Invoice, for older invoices.\n\n" +
            "Keep each store's invoices separate. If Google requires verification delivery help, contact your HISAB KITAB administrator.", "Selective Forwarding");
        var grid = WinTheme.Grid(); grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; grid.ScrollBars = ScrollBars.Both;
        root.Controls.Add(grid, 0, 3);
        void RefreshRows()
        {
            grid.DataSource = InvoiceReviewJournal.Read(paths, key).Select(x => new { Time = x.Time.ToLocalTime(), x.Status, File = Path.GetFileName(x.File), x.Detail, Source = x.File }).ToList();
            if (grid.Columns.Contains("Source")) grid.Columns["Source"].Visible = false;
            foreach (DataGridViewColumn column in grid.Columns) column.Width = column.Name == "Detail" ? 420 : column.Name == "File" ? 240 : column.Name == "Time" ? 210 : 160;
        }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
        root.Controls.Add(footer, 0, 4);
        var open = WinTheme.Button("Open Original PDF"); open.AutoSize = true;
        var import = WinTheme.Button("Review / Import Selected"); import.AutoSize = true;
        var close = WinTheme.Button("Close"); close.Click += (_, _) => Close();
        footer.Controls.AddRange([open, import, close]);
        string? SelectedFile() => grid.CurrentRow?.Cells["Source"].Value as string;
        async Task Run(Func<Task> action)
        {
            if (_busy) return; _busy = true; actions.Enabled = false; footer.Enabled = false;
            try { await action(); }
            catch (Exception ex) { status.Text = AppBootstrap.RedactSensitiveText(ex.Message); MessageBox.Show(this, status.Text, "Invoice Inbox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _busy = false; actions.Enabled = true; footer.Enabled = true; RefreshRows(); }
        }
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        open.Click += (_, _) => { var file = SelectedFile(); if (File.Exists(file)) Process.Start(new ProcessStartInfo(file!) { UseShellExecute = true }); };
        import.Click += async (_, _) => { var file = SelectedFile(); if (File.Exists(file)) await Run(() => review(file!)); };
        connect.Click += async (_, _) => await Run(async () =>
        {
            if (!service.GmailOAuth.IsClientConfigured) throw new InvalidOperationException("Google connection is not configured in this installation. Use the store invoice address or contact support.");
            var email = await service.GmailOAuth.ConnectAsync(key, service.GetSettings(key).EmailAddress);
            var settings = service.GetSettings(key); settings.EmailAddress = email; settings.Provider = "Gmail (Google Sign-In)";
            settings.AuthenticationMode = "GoogleOAuth"; settings.OAuthAuthorized = true; settings.Enabled = true; settings.PasswordOrAppPassword = "";
            service.SaveSettings(key, settings); InvoiceEmailBackgroundSyncService.EnsureTask(service);
            status.Text = "Connected: " + email + ". Invoice checks are enabled. Choose Check Invoices to collect reports now.";
        });
        disconnect.Click += async (_, _) => await Run(async () =>
        {
            if (MessageBox.Show(this, "Disconnect Gmail for this store? Existing purchases remain available.", "Disconnect Gmail", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await service.GmailOAuth.DisconnectAsync(key); var settings = service.GetSettings(key); settings.Enabled = false; settings.OAuthAuthorized = false;
            service.SaveSettings(key, settings); status.Text = "Gmail disconnected.";
        });
        sync.Click += async (_, _) => await Run(async () =>
        {
            status.Text = "Checking invoice PDFs…"; var messages = new List<string>();
            // Each source reports its own result so a mailbox error cannot hide an inbox result.
            if (service.GetSettings(key).Enabled)
                try { var r = await service.SyncAsync(key, storeId, userId, userName, older.Checked ? DateOnly.FromDateTime(month.Value) : null);
                    messages.Add($"Gmail: {r.MessagesChecked} emails checked · {r.AttachmentsFound} PDFs found · {r.InvoicesImported} Imported · {r.DuplicatesSkipped} Already Imported · {r.NeedsReview} Needs Review"
                        + (r.NeedsReview > 0 ? "\nNeeds Review PDFs were fetched. Select one below to see why it was not posted, then use Review / Import Selected." : "")); }
                catch (Exception ex) { messages.Add("Gmail: " + AppBootstrap.RedactSensitiveText(ex.Message)); }
            if (!string.IsNullOrWhiteSpace(business?.InvoiceInboxApiUrl))
                try { var r = await new CloudInvoiceInboxService(paths, service).SyncAsync(business, key, storeId, userId, userName);
                    messages.Add($"Store inbox: {r.InvoicesImported} Imported · {r.DuplicatesSkipped} Already Imported · {r.NeedsReview} Needs Review"); }
                catch (Exception ex) { messages.Add("Store inbox: " + AppBootstrap.RedactSensitiveText(ex.Message)); }
            status.Text = messages.Count == 0 ? "Connect Gmail or ask your administrator to configure a store invoice address." : string.Join("\n", messages);
        });
        RefreshRows();
    }
}
