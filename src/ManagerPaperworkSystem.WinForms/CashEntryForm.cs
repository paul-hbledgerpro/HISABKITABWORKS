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
        Func<CashOnHandEntry, string, Task> saveEntry)
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        WinTheme.Apply(this);
        Text = isPayout ? "Record Payout" : "Add Cash";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(580, isPayout ? 570 : 380);
        MinimumSize = new Size(400, 300);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3
        };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(new Label
        {
            Text = isPayout ? "RECORD PAYOUT\nCash paid out reduces your cash balance." : "ADD CASH\nCash received increases your cash balance.",
            Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 16),
            Font = WinTheme.BoldFont(12), ForeColor = WinTheme.BlueDark
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
        vendor.Name = "CashEntryVendor";
        vendor.FlatStyle = FlatStyle.Standard;
        vendor.DropDownStyle = ComboBoxStyle.DropDown;
        vendor.MaxLength = 200;
        vendor.AutoCompleteMode = AutoCompleteMode.Suggest;
        vendor.AutoCompleteSource = AutoCompleteSource.ListItems;
        vendor.DisplayMember = nameof(Vendor.Name);
        vendor.Items.AddRange(vendors.Cast<object>().ToArray());
        vendor.SelectedIndex = -1;
        var purpose = WinTheme.ComboBox();
        purpose.Name = "CashEntryPurpose";
        purpose.FlatStyle = FlatStyle.Standard;
        purpose.DisplayMember = nameof(Purpose.Name);
        var purposeOptions = purposes.ToList();
        var payoutPurpose = purposeOptions.FirstOrDefault(x =>
            string.Equals(x.Name.Trim(), "Payout", StringComparison.OrdinalIgnoreCase));
        if (isPayout && payoutPurpose is null)
        {
            // This is only a display option. Persist it together with a saved payout.
            payoutPurpose = new Purpose { Name = "Payout" };
            purposeOptions.Insert(0, payoutPurpose);
        }
        purpose.Items.AddRange(purposeOptions.Cast<object>().ToArray());
        purpose.SelectedItem = isPayout ? payoutPurpose : null;
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
            Field("Vendor (optional — type or select)", vendor);
            Field("Purpose", purpose);
        }
        Field(isPayout ? "Description / reason (optional)" : "Note (optional)", note);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 10, 0, 0) };
        buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        var save = WinTheme.Button(isPayout ? "Save Payout" : "Save Cash", true);
        save.Name = "CashEntrySave";
        save.Dock = DockStyle.Fill;
        save.AutoSize = true;
        save.MinimumSize = new Size(0, 44);
        var cancel = WinTheme.Button("Cancel");
        cancel.Dock = DockStyle.Fill;
        cancel.AutoSize = true;
        cancel.MinimumSize = new Size(0, 44);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(save, 0, 0);
        buttons.Controls.Add(cancel, 1, 0);
        shell.Controls.Add(buttons, 0, 2);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
            amount.Focus();
        };
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
            // Capture the final edit before controls change state during the save.
            var vendorName = isPayout ? vendor.Text.Trim() : "";
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
                    PurposeId = isPayout && purpose.SelectedItem is Purpose { Id: > 0 } selectedPurpose ? selectedPurpose.Id : null,
                    Description = note.Text.Trim()
                }, vendorName);
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
                if (DialogResult != DialogResult.OK && isPayout)
                {
                    vendor.SelectedIndex = -1;
                    vendor.Text = vendorName;
                }
            }
        };
    }
}
