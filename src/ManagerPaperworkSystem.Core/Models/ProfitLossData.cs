namespace ManagerPaperworkSystem.Core.Models;

/// <summary>
/// Data for Profit & Loss calculations.
/// Includes all data sources: Shift Logs, Cash On Hand, Check Payouts, Purchases, Bank Statements.
/// </summary>
public class ProfitLossData
{
    // ── REVENUE ──
    public decimal GrossSales { get; set; }        // ShiftLogs.NetSales
    public decimal SalesTax { get; set; }           // ShiftLogs.Tax
    public decimal BankDeposits { get; set; }       // BankStatementTransactions.Credit (category: Deposit/Income)
    public decimal TotalRevenue => GrossSales + SalesTax + BankDeposits;

    // ── EXPENSES (Manual) ──
    public decimal Purchases { get; set; }          // PurchaseInvoices.Total
    public decimal CashPayouts { get; set; }        // CashOnHand (IsPayout=true)
    public decimal CheckPayouts { get; set; }       // CheckPayouts.CheckAmount

    // ── EXPENSES (Bank Statement by Category) ──
    public decimal Utilities { get; set; }
    public decimal Rent { get; set; }
    public decimal Payroll { get; set; }
    public decimal Insurance { get; set; }
    public decimal BankFees { get; set; }
    public decimal Taxes { get; set; }
    public decimal LoanDebt { get; set; }
    public decimal OtherBankExpenses { get; set; }

    public bool HasBankStatementData =>
        Utilities != 0 || Rent != 0 || Payroll != 0 || Insurance != 0 ||
        BankFees != 0 || Taxes != 0 || LoanDebt != 0 || OtherBankExpenses != 0;

    // ── TOTALS ──
    public decimal TotalManualExpenses => Purchases + CashPayouts + CheckPayouts;
    public decimal TotalBankExpenses => Utilities + Rent + Payroll + Insurance + BankFees + Taxes + LoanDebt + OtherBankExpenses;
    public decimal TotalExpenses => TotalManualExpenses + TotalBankExpenses;

    public decimal NetProfitLoss => TotalRevenue - TotalExpenses;
    public bool IsProfit => NetProfitLoss >= 0;
}
