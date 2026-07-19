using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ManagerPaperworkSystem.Core.Models;

public abstract class Entity
{
    [Key]
    public int Id { get; set; }
}

public sealed class AppSettings : Entity
{
    [MaxLength(200)]
    public string StoreName { get; set; } = "";

    [MaxLength(400)]
    public string StoreAddress { get; set; } = "";

    public ReportType DefaultReportType { get; set; } = ReportType.ShiftLog;

    // UI preference: 0 = Laptop 15", 1 = PC 21"
    public int ScreenMode { get; set; } = 0;

    // Multi-store: keep last-selected store for the user.
    public int DefaultStoreId { get; set; } = 1;
    public int LastStoreId { get; set; } = 1;

    public bool SmsGatewayEnabled { get; set; }

    [MaxLength(500)]
    public string SmsGatewayUrl { get; set; } = "";

    [MaxLength(200)]
    public string SmsGatewayUsername { get; set; } = "";

    public byte[] SmsGatewayPasswordEncrypted { get; set; } = Array.Empty<byte>();

    [MaxLength(254)]
    public string AccountantEmail { get; set; } = "";

    public bool AutoEmailBankStatementOnFifth { get; set; }
}

public enum ReportType
{
    ShiftLog = 1,
    CashOnHand = 2,
    CheckPayouts = 3,
    All = 4,
    SalesSummaryByDate = 5,
    ProfitLoss = 6,
    Payroll = 7
}

public enum UserRole
{
    OwnerAdmin = 1,
    Manager = 2
}

public sealed class UserAccount : Entity
{
    [MaxLength(80)]
    public string FirstName { get; set; } = "";

    [MaxLength(80)]
    public string LastName { get; set; } = "";

    public UserRole Role { get; set; } = UserRole.Manager;

    [MaxLength(80)]
    public string Username { get; set; } = "";

    [MaxLength(200)]
    public string Email { get; set; } = "";

    [MaxLength(200)]
    public string PasswordHashBase64 { get; set; } = "";

    [MaxLength(200)]
    public string SaltBase64 { get; set; } = "";

    // Password reset (security question)
    [MaxLength(240)]
    public string SecurityQuestion { get; set; } = "";

    [MaxLength(200)]
    public string SecurityAnswerHashBase64 { get; set; } = "";

    [MaxLength(200)]
    public string SecurityAnswerSaltBase64 { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
    public DateTime? LastChangedUtc { get; set; }

    [NotMapped]
    public string DisplayName => (FirstName + " " + LastName).Trim();
}

// ==========================
// Multi-store
// ==========================

public sealed class Store : Entity
{
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(400)]
    public string Address { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Vendor : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";
}

public sealed class Purpose : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";
}

// ==========================
// Shift / COH / Checks
// ==========================

public sealed class ShiftLogEntry : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    public DateOnly Date { get; set; }

    [MaxLength(100)]
    public string Employee { get; set; } = "";

    [MaxLength(20)]
    public string ShiftNo { get; set; } = "";

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CardTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashDropReceived { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RegisterPayout { get; set; }

    [MaxLength(300)]
    public string PayoutReason { get; set; } = "";

    public int? PosSalesSummaryId { get; set; }

    [NotMapped]
    public decimal GrossSales => CashTotal + CardTotal + Tax;

    [NotMapped]
    public decimal Variance => CashDropReceived + RegisterPayout - CashTotal;

    // Audit
    public int CreatedByUserId { get; set; }

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    // Corrections are additive entries; we store metadata for reports.
    public bool IsCorrection { get; set; }
    public int? CorrectsId { get; set; }

    [MaxLength(300)]
    public string CorrectionReason { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CashOnHandEntry : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    public DateOnly Date { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashAdded { get; set; }

    [MaxLength(50)]
    public string Reference { get; set; } = "";

    public bool IsPayout { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PayoutAmount { get; set; }

    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public int? PurposeId { get; set; }
    public Purpose? Purpose { get; set; }

    [MaxLength(400)]
    public string Description { get; set; } = "";

    // Audit
    public int CreatedByUserId { get; set; }

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    public bool IsCorrection { get; set; }
    public int? CorrectsId { get; set; }

    [MaxLength(300)]
    public string CorrectionReason { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CheckPayout : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    public DateOnly Date { get; set; }

    [MaxLength(200)]
    public string VendorName { get; set; } = "";

    [MaxLength(400)]
    public string Description { get; set; } = "";

    [Column(TypeName = "decimal(18,2)")]
    public decimal CheckAmount { get; set; }

    [MaxLength(50)]
    public string CheckNumber { get; set; } = "";

    public bool Cleared { get; set; }

    // Audit
    public int CreatedByUserId { get; set; }

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    public bool IsCorrection { get; set; }
    public int? CorrectsId { get; set; }

    [MaxLength(300)]
    public string CorrectionReason { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

// ==========================
// POS Cash + Sales Summaries
// ==========================

public sealed class PosSalesSummary : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    public DateOnly ReportFrom { get; set; }
    public DateOnly ReportTo { get; set; }

    [MaxLength(120)]
    public string SourceSystem { get; set; } = "AdventPOS";

    [MaxLength(200)]
    public string ReportedStoreName { get; set; } = "";

    [MaxLength(260)]
    public string SourceFileName { get; set; } = "";

    [MaxLength(500)]
    public string SourceFilePath { get; set; } = "";

    [MaxLength(64)]
    public string SourceFileSha256 { get; set; } = "";

    public int TenderTransactionCount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossAmountReceived { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GiftCardRedeemed { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NonRevenueReceived { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NonRevenueReturned { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NonRevenueAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Taxes { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TaxableSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NonTaxableSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RoundingOffset { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CardSales { get; set; }

    public int CustomerTransactionCount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CustomerAverageSale { get; set; }

    public int UserLoginCount { get; set; }
    public int DeleteVoidCount { get; set; }
    public int NoSaleCount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VoidDeleteAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDiscount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DepartmentQuantity { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DepartmentSales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DepartmentCost { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DepartmentProfit { get; set; }

    [Column(TypeName = "decimal(9,4)")]
    public decimal DepartmentProfitPercent { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashDropReceived { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RegisterPayout { get; set; }

    [MaxLength(300)]
    public string PayoutReason { get; set; } = "";

    public bool IsReconciled { get; set; }
    public int? ReconciledByUserId { get; set; }

    [MaxLength(120)]
    public string ReconciledByName { get; set; } = "";

    public DateTime? ReconciledUtc { get; set; }

    [NotMapped]
    public decimal CashVariance => CashDropReceived + RegisterPayout - CashSales;

    public int ImportedByUserId { get; set; }

    [MaxLength(120)]
    public string ImportedByName { get; set; } = "";

    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;

    public List<PosSalesTenderLine> TenderLines { get; set; } = new();
    public List<PosSalesHourlyLine> HourlyLines { get; set; } = new();
    public List<PosSalesDepartmentLine> DepartmentLines { get; set; } = new();
}

public sealed class PosSalesTenderLine : Entity
{
    public int PosSalesSummaryId { get; set; }
    public PosSalesSummary? PosSalesSummary { get; set; }

    [MaxLength(80)]
    public string TenderType { get; set; } = "";

    public int TransactionCount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
}

public sealed class PosSalesHourlyLine : Entity
{
    public int PosSalesSummaryId { get; set; }
    public PosSalesSummary? PosSalesSummary { get; set; }

    [MaxLength(80)]
    public string TimePeriod { get; set; } = "";

    public int TransactionCount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
}

public sealed class PosSalesDepartmentLine : Entity
{
    public int PosSalesSummaryId { get; set; }
    public PosSalesSummary? PosSalesSummary { get; set; }

    [MaxLength(180)]
    public string Department { get; set; } = "";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Sales { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Cost { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Profit { get; set; }

    [Column(TypeName = "decimal(9,4)")]
    public decimal ProfitPercent { get; set; }

    [Column(TypeName = "decimal(9,4)")]
    public decimal SalesPercent { get; set; }
}

// ==========================
// Purchases + Cost Tracking
// ==========================

public enum PriceChangeDirection
{
    Down = -1,
    Up = 1
}

public sealed class PurchaseInvoice : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    [MaxLength(200)]
    public string VendorName { get; set; } = "";

    [MaxLength(100)]
    public string InvoiceNumber { get; set; } = "";

    public DateOnly InvoiceDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    [MaxLength(500)]
    public string FilePath { get; set; } = "";

    [MaxLength(500)]
    public string Notes { get; set; } = "";

    public int CreatedByUserId { get; set; }

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<PurchaseInvoiceLine> Lines { get; set; } = new();
}

public sealed class PurchaseInvoiceLine : Entity
{
    public int PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }

    [MaxLength(260)]
    public string ProductName { get; set; } = "";

    // Optional structured fields (used by vendor-specific invoice importers)
    // These are nullable/optional in the DB (added via DbMigrator) so older DBs keep working.

    [MaxLength(80)]
    public string ItemCode { get; set; } = ""; // UPC or vendor SKU

    [Column(TypeName = "decimal(18,2)")]
    public decimal OrdQuantity { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ShipQuantity { get; set; }

    public int? VolumeMl { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Tax { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Price { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Amount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitCost { get; set; }
}

public sealed class ProductCost : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    [MaxLength(260)]
    public string ProductKey { get; set; } = ""; // normalized key for matching (e.g., upper(trim(name)))

    [MaxLength(260)]
    public string ProductName { get; set; } = "";

    [MaxLength(80)]
    public string Sku { get; set; } = ""; // UPC or vendor SKU code

    [Column(TypeName = "decimal(18,4)")]
    public decimal LastUnitCost { get; set; }

    public DateOnly LastInvoiceDate { get; set; }

    [MaxLength(200)]
    public string LastVendorName { get; set; } = "";

    [MaxLength(100)]
    public string LastInvoiceNumber { get; set; } = "";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PriceAlert : Entity
{
    public int StoreId { get; set; } = 1;
    public Store? Store { get; set; }

    [MaxLength(260)]
    public string ProductKey { get; set; } = "";

    [MaxLength(260)]
    public string ProductName { get; set; } = "";

    [MaxLength(80)]
    public string Sku { get; set; } = ""; // UPC or vendor SKU code

    [Column(TypeName = "decimal(18,4)")]
    public decimal OldUnitCost { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal NewUnitCost { get; set; }

    public PriceChangeDirection Direction { get; set; }
    
    public PriceAlertType AlertType { get; set; } = PriceAlertType.PriceChange;

    [MaxLength(200)]
    public string VendorName { get; set; } = "";
    
    [MaxLength(200)]
    public string OtherVendorName { get; set; } = ""; // For cross-vendor alerts

    [MaxLength(100)]
    public string InvoiceNumber { get; set; } = "";

    public DateOnly InvoiceDate { get; set; }

    public int? PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }

    public bool IsRead { get; set; }
    public DateTime? ReadUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum PriceAlertType
{
    PriceChange = 0,        // Same product, same vendor, price changed
    CrossVendorPrice = 1,   // Same product from different vendor with price variation
    CrossVendorNew = 2      // Same product now available from a new vendor
}
