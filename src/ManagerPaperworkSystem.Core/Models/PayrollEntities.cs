using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ManagerPaperworkSystem.Core.Models;

public enum PayFrequency
{
    Weekly = 1,
    Biweekly = 2,
    Semimonthly = 3,
    Monthly = 4
}

public enum EmployeePayType
{
    Hourly = 1,
    Salary = 2
}

public enum FederalFilingStatus
{
    SingleOrMarriedFilingSeparately = 1,
    MarriedFilingJointly = 2,
    HeadOfHousehold = 3
}

public enum EmployeeDocumentType
{
    FederalW4 = 1,
    StateWithholding = 2,
    FormI9 = 3,
    DriversLicenseOrId = 4,
    Other = 99
}

public enum ScheduleShiftStatus
{
    Draft = 0,
    Published = 1,
    Completed = 2,
    Cancelled = 3
}

public enum PayrollRunStatus
{
    Draft = 0,
    Approved = 1,
    Finalized = 2,
    Voided = 3
}

public sealed class Employee : Entity
{
    public int StoreId { get; set; } = 1;

    [MaxLength(30)]
    public string EmployeeNumber { get; set; } = "";

    [MaxLength(80)]
    public string FirstName { get; set; } = "";

    [MaxLength(10)]
    public string MiddleInitial { get; set; } = "";

    [MaxLength(80)]
    public string LastName { get; set; } = "";

    [MaxLength(300)]
    public string Address { get; set; } = "";

    [MaxLength(100)]
    public string City { get; set; } = "";

    [MaxLength(20)]
    public string State { get; set; } = "";

    [MaxLength(20)]
    public string Zip { get; set; } = "";

    [MaxLength(40)]
    public string Phone { get; set; } = "";

    [MaxLength(200)]
    public string Email { get; set; } = "";

    public byte[] EncryptedSsn { get; set; } = Array.Empty<byte>();

    [MaxLength(4)]
    public string SsnLast4 { get; set; } = "";

    [Column(TypeName = "decimal(18,4)")]
    public decimal PayRate { get; set; }

    public EmployeePayType PayType { get; set; } = EmployeePayType.Hourly;
    public PayFrequency PayFrequency { get; set; } = PayFrequency.Biweekly;
    public bool IsOvertimeEligible { get; set; } = true;

    [Column(TypeName = "decimal(8,4)")]
    public decimal HolidayMultiplier { get; set; } = 1.5m;

    [MaxLength(20)]
    public string WorkState { get; set; } = "IL";

    [MaxLength(20)]
    public string ResidenceState { get; set; } = "IL";

    public DateOnly HireDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? TerminationDate { get; set; }
    public bool IsActive { get; set; } = true;

    public FederalFilingStatus FederalFilingStatus { get; set; } = FederalFilingStatus.SingleOrMarriedFilingSeparately;
    public bool FederalMultipleJobs { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FederalDependentsCredit { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FederalOtherIncome { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FederalDeductions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FederalExtraWithholding { get; set; }

    public bool FederalExempt { get; set; }

    [MaxLength(50)]
    public string StateFilingStatus { get; set; } = "Single";

    public int StateAllowances { get; set; }
    public int StateAdditionalAllowances { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StateDeductions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StateCredits { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StateExtraWithholding { get; set; }

    public bool StateExempt { get; set; }

    [MaxLength(4000)]
    public string StateFormDataJson { get; set; } = "{}";

    // Retained for backward compatibility with existing Illinois employee records.
    public int IllinoisLine1Allowances { get; set; }
    public int IllinoisLine2Allowances { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal IllinoisExtraWithholding { get; set; }

    public bool W4OnFile { get; set; }
    public bool StateWithholdingOnFile { get; set; }

    [MaxLength(200)]
    public string EmergencyContactName { get; set; } = "";

    [MaxLength(40)]
    public string EmergencyContactPhone { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public string FullName => string.Join(" ", new[] { FirstName, MiddleInitial, LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));

    [NotMapped]
    public string MaskedSsn => string.IsNullOrWhiteSpace(SsnLast4) ? "Not entered" : $"***-**-{SsnLast4}";
}

public sealed class EmployeeDocument : Entity
{
    public int StoreId { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public EmployeeDocumentType DocumentType { get; set; }

    [MaxLength(260)]
    public string FileName { get; set; } = "";

    [MaxLength(120)]
    public string ContentType { get; set; } = "application/octet-stream";

    public byte[] EncryptedContent { get; set; } = Array.Empty<byte>();

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ScheduleShift : Entity
{
    public int StoreId { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly ShiftDate { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int UnpaidBreakMinutes { get; set; }
    public ScheduleShiftStatus Status { get; set; } = ScheduleShiftStatus.Draft;

    [MaxLength(500)]
    public string Notes { get; set; } = "";

    [MaxLength(120)]
    public string UpdatedByName { get; set; } = "";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public decimal ScheduledHours
    {
        get
        {
            var end = EndTime <= StartTime ? EndTime.Add(TimeSpan.FromDays(1)) : EndTime;
            var minutes = Math.Max(0, (end - StartTime).TotalMinutes - Math.Max(0, UnpaidBreakMinutes));
            return Math.Round((decimal)minutes / 60m, 2, MidpointRounding.AwayFromZero);
        }
    }
}

/// <summary>
/// Administrator-entered hours for a payroll period. These are independent of
/// the schedule so owners/managers who are not scheduled can still be paid.
/// </summary>
public sealed class EmployeePeriodHours : Entity
{
    public int StoreId { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RegularHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OvertimeHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal HolidayHours { get; set; }

    [MaxLength(500)]
    public string Notes { get; set; } = "";

    [MaxLength(120)]
    public string UpdatedByName { get; set; } = "";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PayrollRun : Entity
{
    public int StoreId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly PayDate { get; set; }
    public PayFrequency PayFrequency { get; set; }
    public int TaxYear { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;

    [MaxLength(100)]
    public string TaxRuleSetId { get; set; } = "";

    [MaxLength(40)]
    public string TaxRuleVersion { get; set; } = "";

    [MaxLength(64)]
    public string TaxRuleSha256 { get; set; } = "";

    [MaxLength(1000)]
    public string TaxRuleSources { get; set; } = "";

    public DateTime? TaxRulesVerifiedUtc { get; set; }

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(120)]
    public string ApprovedByName { get; set; } = "";

    public DateTime? ApprovedUtc { get; set; }

    [MaxLength(120)]
    public string FinalizedByName { get; set; } = "";

    public DateTime? FinalizedUtc { get; set; }
    public List<PayrollEntry> Entries { get; set; } = new();
}

public sealed class PayrollEntry : Entity
{
    public int PayrollRunId { get; set; }
    public PayrollRun? PayrollRun { get; set; }
    public int EmployeeId { get; set; }

    [MaxLength(200)]
    public string EmployeeName { get; set; } = "";

    [Column(TypeName = "decimal(18,4)")]
    public decimal PayRate { get; set; }

    public EmployeePayType PayType { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ScheduledHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RegularHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OvertimeHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal HolidayHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BonusPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RegularPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OvertimePay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal HolidayPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CashAdvanceDeduction { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OtherDeduction { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FederalWithholding { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SocialSecurityWithholding { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MedicareWithholding { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StateWithholding { get; set; }

    [MaxLength(2)]
    public string WorkState { get; set; } = "";

    [MaxLength(100)]
    public string StateTaxRuleId { get; set; } = "";

    [MaxLength(40)]
    public string StateTaxRuleVersion { get; set; } = "";

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossPayYtd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FederalWithholdingYtd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SocialSecurityWithholdingYtd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MedicareWithholdingYtd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StateWithholdingYtd { get; set; }

    [MaxLength(50)]
    public string CheckNumber { get; set; } = "";

    [MaxLength(500)]
    public string OverrideReason { get; set; } = "";
}

public sealed class PayrollAuditEntry : Entity
{
    public int StoreId { get; set; }
    public int PayrollRunId { get; set; }
    public int? PayrollEntryId { get; set; }

    [MaxLength(80)]
    public string Action { get; set; } = "";

    [MaxLength(1000)]
    public string Details { get; set; } = "";

    [MaxLength(120)]
    public string PerformedByName { get; set; } = "";

    public DateTime PerformedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ScheduleNotification : Entity
{
    public int StoreId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly ScheduleFrom { get; set; }
    public DateOnly ScheduleTo { get; set; }

    [MaxLength(40)]
    public string PhoneNumber { get; set; } = "";

    [MaxLength(2000)]
    public string MessageText { get; set; } = "";

    [MaxLength(30)]
    public string Status { get; set; } = "Pending";

    [MaxLength(1000)]
    public string GatewayResponse { get; set; } = "";

    [MaxLength(120)]
    public string CreatedByName { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentUtc { get; set; }
}
