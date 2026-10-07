using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ManagerPaperworkSystem.Core.Models;

public sealed class PendingShiftDrop
{
    public int Id { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public int StoreId { get; set; }
    public Guid ConfigurationId { get; set; }
    [MaxLength(300)] public string PortalStoreName { get; set; } = "";
    [MaxLength(20)] public string Batch { get; set; } = "";
    [Column(TypeName="decimal(18,2)")] public decimal Drop { get; set; }
    [Column(TypeName="decimal(18,2)")] public decimal Payout { get; set; }
    [MaxLength(300)] public string Reason { get; set; } = "";
    public int UserId { get; set; }
    [MaxLength(120)] public string UserName { get; set; } = "";
    [MaxLength(30)] public string Status { get; set; } = "Pending";
    [MaxLength(1000)] public string Message { get; set; } = "Waiting for batch";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? NotifiedUtc { get; set; }
    public int? ShiftId { get; set; }
    [Column(TypeName="decimal(18,2)")] public decimal? Variance { get; set; }
}

public sealed class LedgerMonth
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public DateOnly Month { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedUtc { get; set; }
    [MaxLength(120)] public string ClosedBy { get; set; } = "";
    [Column(TypeName="decimal(18,2)")] public decimal? OpeningCash { get; set; }
    [MaxLength(120)] public string OpeningBy { get; set; } = "";
}

public sealed class ShiftPayoutRollup
{
    public int Id { get; set; }
    public int SummaryId { get; set; }
    [Column(TypeName="decimal(18,2)")] public decimal AppliedAmount { get; set; }
    public string AppliedReason { get; set; } = "";
}
