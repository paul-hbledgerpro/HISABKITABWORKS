using System.Globalization;
using System.Windows;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.UI.Views;

public partial class CashCorrectionWindow : Window
{
    private readonly CashOnHandEntry _original;
    private readonly int _userId;
    private readonly string _userName;
    private readonly IReadOnlyList<Vendor> _vendors;
    private readonly IReadOnlyList<Purpose> _purposes;

    public CashOnHandEntry? ResultEntry { get; private set; }

    public CashCorrectionWindow(CashOnHandEntry original, int userId, string userName,
        IReadOnlyList<Vendor> vendors, IReadOnlyList<Purpose> purposes)
    {
        InitializeComponent();
        _original = original;
        _userId = userId;
        _userName = userName;
        _vendors = vendors;
        _purposes = purposes;

        txtOriginal.Text = $"ID: {original.Id}   Date: {original.Date}   Is Payout: {(original.IsPayout ? "Yes" : "No")}\n" +
                           $"Cash Added: {original.CashAdded}   Payout: {original.PayoutAmount}\n" +
                           $"Vendor: {original.Vendor?.Name ?? ""}   Purpose: {original.Purpose?.Name ?? ""}\n" +
                           $"Desc: {original.Description}";

        dpDate.SelectedDate = original.Date.ToDateTime(TimeOnly.MinValue);
        txtCashAdded.Text = original.CashAdded.ToString(CultureInfo.CurrentCulture);
        cmbIsPayout.ItemsSource = new List<string> { "No", "Yes" };
        cmbIsPayout.SelectedIndex = original.IsPayout ? 1 : 0;
        txtPayoutAmount.Text = original.PayoutAmount.ToString(CultureInfo.CurrentCulture);

        cmbVendor.ItemsSource = _vendors;
        cmbVendor.DisplayMemberPath = "Name";
        cmbVendor.SelectedValuePath = "Id";
        if (original.VendorId.HasValue)
            cmbVendor.SelectedValue = original.VendorId.Value;

        cmbPurpose.ItemsSource = _purposes;
        cmbPurpose.DisplayMemberPath = "Name";
        cmbPurpose.SelectedValuePath = "Id";
        if (original.PurposeId.HasValue)
            cmbPurpose.SelectedValue = original.PurposeId.Value;

        txtDesc.Text = original.Description;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static decimal ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        text = text.Trim();
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.CurrentCulture, out var d))
            return d;
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out d))
            return d;
        throw new FormatException($"Invalid amount: {text}");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";

        try
        {
            if (dpDate.SelectedDate is null)
                throw new Exception("Date is required.");

            var reason = (txtReason.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(reason))
                throw new Exception("Reason is required for a correction.");

            var isPayout = (cmbIsPayout.SelectedIndex == 1);

            ResultEntry = new CashOnHandEntry
            {
                StoreId = _original.StoreId,
                Date = DateOnly.FromDateTime(dpDate.SelectedDate.Value),
                CashAdded = ParseMoney(txtCashAdded.Text),
                IsPayout = isPayout,
                PayoutAmount = ParseMoney(txtPayoutAmount.Text),
                VendorId = cmbVendor.SelectedValue as int?,
                PurposeId = cmbPurpose.SelectedValue as int?,
                Description = (txtDesc.Text ?? "").Trim(),

                CreatedByUserId = _userId,
                CreatedByName = _userName,

                IsCorrection = true,
                CorrectsId = _original.Id,
                CorrectionReason = reason,
                CreatedUtc = DateTime.UtcNow
            };

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}
