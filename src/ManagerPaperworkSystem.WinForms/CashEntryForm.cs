using System.Globalization;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.WinForms;

/// <summary>A new cash movement has exactly one direction and one amount.</summary>
internal sealed class CashEntryForm : Form
{
    private bool _saving;

    public CashEntryForm(
        bool isPayout,
        IReadOnlyList<Vendor> vendors,
        IReadOnlyList<Purpose> purposes,
        Func<CashOnHandEntry, Task> saveEntry)
    {
        WinTheme.Apply(this);
        Text = isPayout ? "Record Payout" : "Add Cash";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, isPayout ? 500 : 350);
        MinimumSize = new Size(400, 300);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        shell.Controls.Add(new Label
        {
            Text = isPayout ? "RECORD PAYOUT\nCash paid out reduces your cash balance." : "ADD CASH\nCash received increases your cash balance.",
            Dock = DockStyle.Fill, Font = WinTheme.BoldFont(12), ForeColor = WinTheme.BlueDark
        }, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 12, 0)
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(fields);
        shell.Controls.Add(scroll, 0, 1);
        Controls.Add(shell);

        void Field(string label, Control control)
        {
            var row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Font = WinTheme.BoldFont(10),
                Margin = new Padding(0, 7, 0, 4)
            }, 0, row);
            row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0, 0, 0, 5);
            fields.Controls.Add(control, 0, row);
        }

        var date = WinTheme.DatePicker();
        date.Value = DateTime.Today;
        var amount = new TextBox { TextAlign = HorizontalAlignment.Right, Font = WinTheme.BodyFont(14), PlaceholderText = "0.00", Name = "CashEntryAmount" };
        var vendor = WinTheme.ComboBox();
        vendor.DataSource = vendors.ToList();
        vendor.DisplayMember = nameof(Vendor.Name);
        vendor.ValueMember = nameof(Vendor.Id);
        vendor.SelectedIndex = -1;
        var purpose = WinTheme.ComboBox();
        purpose.DataSource = purposes.ToList();
        purpose.DisplayMember = nameof(Purpose.Name);
        purpose.ValueMember = nameof(Purpose.Id);
        purpose.SelectedIndex = -1;
        var note = new TextBox
        {
            MaxLength = 400, Multiline = true, Height = 64, ScrollBars = ScrollBars.Vertical,
            Name = "CashEntryNote",
            PlaceholderText = isPayout ? "Who was paid and what was it for?" : "Where did this cash come from? (optional)"
        };
        Field("Date", date);
        Field(isPayout ? "Amount paid *" : "Cash received *", amount);
        if (isPayout)
        {
            Field("Vendor (optional)", vendor);
            Field("Purpose (or describe below)", purpose);
        }
        Field(isPayout ? "Description / reason" : "Note (optional)", note);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        var save = WinTheme.Button(isPayout ? "Save Payout" : "Save Cash", true);
        save.Name = "CashEntrySave";
        save.Dock = DockStyle.Fill;
        var cancel = WinTheme.Button("Cancel");
        cancel.Dock = DockStyle.Fill;
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(save, 0, 0);
        buttons.Controls.Add(cancel, 1, 0);
        shell.Controls.Add(buttons, 0, 2);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => amount.Focus();
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };

        save.Click += async (_, _) =>
        {
            if (_saving) return;
            if (!decimal.TryParse(amount.Text.Trim(), NumberStyles.Currency, CultureInfo.CurrentCulture, out var value) ||
                value <= 0 || value > 9999999999999999.99m || decimal.Round(value, 2) != value)
            {
                MessageBox.Show(this, "Enter an amount greater than zero with no more than two decimal places.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                amount.Focus();
                return;
            }
            if (isPayout && purpose.SelectedItem is not Purpose && string.IsNullOrWhiteSpace(note.Text))
            {
                MessageBox.Show(this, "Select a purpose or enter a short description of the payout.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                note.Focus();
                return;
            }
            _saving = true;
            save.Enabled = cancel.Enabled = scroll.Enabled = false;
            try
            {
                await saveEntry(new CashOnHandEntry
                {
                    Date = DateOnly.FromDateTime(date.Value),
                    IsPayout = isPayout,
                    CashAdded = isPayout ? 0m : value,
                    PayoutAmount = isPayout ? value : 0m,
                    VendorId = isPayout && vendor.SelectedItem is Vendor selectedVendor ? selectedVendor.Id : null,
                    PurposeId = isPayout && purpose.SelectedItem is Purpose selectedPurpose ? selectedPurpose.Id : null,
                    Description = note.Text.Trim()
                });
                _saving = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The entry could not be saved. Your details are still here.\n\n" + AppBootstrap.RedactSensitiveText(ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _saving = false;
                save.Enabled = cancel.Enabled = scroll.Enabled = true;
            }
        };
    }
}
