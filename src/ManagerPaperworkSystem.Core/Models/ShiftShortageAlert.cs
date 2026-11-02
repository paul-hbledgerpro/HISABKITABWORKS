using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ManagerPaperworkSystem.Core.Models;

public sealed class ShiftShortageSettings
{
    [Key] public int StoreId { get; set; }
    [MaxLength(100)] public string StoreGuid { get; set; } = "";
    [MaxLength(200)] public string StoreName { get; set; } = "";
    public bool Enabled { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Threshold { get; set; } = 10;
    public string RecipientsJson { get; set; } = "[]";
    public DateTime UpdatedUtc { get; set; }
    [MaxLength(120)] public string UpdatedBy { get; set; } = "";
}

public sealed class ShiftShortageEmail
{
    [Key, MaxLength(64)] public string Id { get; set; } = "";
    public int StoreId { get; set; }
    [MaxLength(100)] public string StoreGuid { get; set; } = "";
    public string RecipientsJson { get; set; } = "[]";
    public string ReportJson { get; set; } = "";
    [MaxLength(30)] public string Status { get; set; } = "Queued";
    public DateTime CreatedUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    [MaxLength(40)] public string LeaseToken { get; set; } = "";
    [MaxLength(500)] public string Message { get; set; } = "Waiting to send";
}

public sealed record ShiftShortageReport(string StoreName, string Batch, DateOnly Date, string Employee,
    decimal Cash, decimal Card, decimal Net, decimal Tax, decimal Drop, decimal Payout,
    string Payee, string Reason, decimal Shortage, decimal Threshold, string EnteredBy,
    DateTime EnteredUtc, bool IsCorrection);
