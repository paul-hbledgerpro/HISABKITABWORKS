using System.Globalization;
using System.Windows;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.UI.Views;

public partial class CheckCorrectionWindow : Window
{
    private readonly CheckPayout _original;
    private readonly int _userId;
    private readonly string _userName;

    public CheckPayout? ResultEntry { get; private set; }

    public CheckCorrectionWindow(CheckPayout original, int userId, string userName)
    {
        InitializeComponent();
        _original = original;
        _userId = userId;
        _userName = userName;

        txtOriginal.Text = $"ID: {original.Id}   Date: {original.Date}   Cleared: {(original.Cleared ? "Yes" : "No")}\n" +
                           $"Vendor: {original.VendorName}\n" +
                           $"Check #: {original.CheckNumber}   Amount: {original.CheckAmount}\n" +
                           $"Desc: {original.Description}";

        dpDate.SelectedDate = original.Date.ToDateTime(TimeOnly.MinValue);
        txtVendor.Text = original.VendorName;
        txtCheckNumber.Text = original.CheckNumber;
        txtDesc.Text = original.Description;
        txtAmount.Text = original.CheckAmount.ToString(CultureInfo.CurrentCulture);
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

            ResultEntry = new CheckPayout
            {
                StoreId = _original.StoreId,
                Date = DateOnly.FromDateTime(dpDate.SelectedDate.Value),
                VendorName = (txtVendor.Text ?? "").Trim(),
                CheckNumber = (txtCheckNumber.Text ?? "").Trim(),
                Description = (txtDesc.Text ?? "").Trim(),
                CheckAmount = ParseMoney(txtAmount.Text),

                // For corrections, we keep cleared as original state.
                Cleared = _original.Cleared,

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
