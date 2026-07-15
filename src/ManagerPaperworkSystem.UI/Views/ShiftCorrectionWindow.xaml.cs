using System.Globalization;
using System.Windows;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.UI.Views;

public partial class ShiftCorrectionWindow : Window
{
    private readonly ShiftLogEntry _original;
    private readonly int _userId;
    private readonly string _userName;
    private System.Windows.Controls.TextBox? _txtPayoutReason;

    public ShiftLogEntry? ResultEntry { get; private set; }

    public ShiftCorrectionWindow(ShiftLogEntry original, int userId, string userName)
    {
        InitializeComponent();
        _original = original;
        _userId = userId;
        _userName = userName;
        InstallPayoutReasonField();

        lblOriginal.Text = $"ID: {original.Id}   Date: {original.Date}   Employee: {original.Employee}   Shift: {original.ShiftNo}\n" +
                           $"Cash: {original.CashTotal}   Card: {original.CardTotal}   Net: {original.NetSales}   Tax: {original.Tax}   Drop: {original.CashDropReceived}   Reg Payout: {original.RegisterPayout}   Payout Reason: {original.PayoutReason}   Variance: {original.Variance}";

        // Prefill corrected fields with original values
        dpDate.SelectedDate = original.Date.ToDateTime(TimeOnly.MinValue);
        txtEmployee.Text = original.Employee;
        txtShiftNo.Text = original.ShiftNo;
        txtCash.Text = original.CashTotal.ToString(CultureInfo.CurrentCulture);
        txtCard.Text = original.CardTotal.ToString(CultureInfo.CurrentCulture);
        txtNetSales.Text = original.NetSales.ToString(CultureInfo.CurrentCulture);
        txtTax.Text = original.Tax.ToString(CultureInfo.CurrentCulture);
        txtDrop.Text = original.CashDropReceived.ToString(CultureInfo.CurrentCulture);
        txtRegPayout.Text = original.RegisterPayout.ToString(CultureInfo.CurrentCulture);
        if (_txtPayoutReason is not null)
            _txtPayoutReason.Text = original.PayoutReason;
    }

    private void InstallPayoutReasonField()
    {
        if (txtRegPayout.Parent is not System.Windows.Controls.Grid grid)
            return;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Payout Reason" });
        _txtPayoutReason = new System.Windows.Controls.TextBox { MaxLength = 300 };
        panel.Children.Add(_txtPayoutReason);

        System.Windows.Controls.Grid.SetRow(panel, 4);
        System.Windows.Controls.Grid.SetColumn(panel, 2);
        grid.Children.Add(panel);
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

            ResultEntry = new ShiftLogEntry
            {
                StoreId = _original.StoreId,
                Date = DateOnly.FromDateTime(dpDate.SelectedDate.Value),
                Employee = (txtEmployee.Text ?? "").Trim(),
                ShiftNo = (txtShiftNo.Text ?? "").Trim(),
                CashTotal = ParseMoney(txtCash.Text),
                CardTotal = ParseMoney(txtCard.Text),
                CashDropReceived = ParseMoney(txtDrop.Text),
                NetSales = ParseMoney(txtNetSales.Text),
                Tax = ParseMoney(txtTax.Text),
                RegisterPayout = ParseMoney(txtRegPayout.Text),
                PayoutReason = _txtPayoutReason?.Text?.Trim() ?? "",

                // audit
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
