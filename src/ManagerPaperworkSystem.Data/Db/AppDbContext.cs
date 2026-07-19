using ManagerPaperworkSystem.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Db;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Purpose> Purposes => Set<Purpose>();

    public DbSet<ShiftLogEntry> ShiftLogs => Set<ShiftLogEntry>();
    public DbSet<CashOnHandEntry> CashOnHand => Set<CashOnHandEntry>();
    public DbSet<CheckPayout> CheckPayouts => Set<CheckPayout>();
    public DbSet<PosSalesSummary> PosSalesSummaries => Set<PosSalesSummary>();
    public DbSet<PosSalesTenderLine> PosSalesTenderLines => Set<PosSalesTenderLine>();
    public DbSet<PosSalesHourlyLine> PosSalesHourlyLines => Set<PosSalesHourlyLine>();
    public DbSet<PosSalesDepartmentLine> PosSalesDepartmentLines => Set<PosSalesDepartmentLine>();

    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<PurchaseInvoiceLine> PurchaseInvoiceLines => Set<PurchaseInvoiceLine>();
    public DbSet<ProductCost> ProductCosts => Set<ProductCost>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<ScheduleShift> ScheduleShifts => Set<ScheduleShift>();
    public DbSet<EmployeePeriodHours> EmployeePeriodHours => Set<EmployeePeriodHours>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollEntry> PayrollEntries => Set<PayrollEntry>();
    public DbSet<PayrollAuditEntry> PayrollAuditEntries => Set<PayrollAuditEntry>();
    public DbSet<ScheduleNotification> ScheduleNotifications => Set<ScheduleNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Map entity names to SQL Server table names
        modelBuilder.Entity<AppSettings>().ToTable("AppSettings");
        modelBuilder.Entity<UserAccount>().ToTable("UserAccounts");
        modelBuilder.Entity<Store>().ToTable("Stores");
        modelBuilder.Entity<Vendor>().ToTable("Vendors");
        modelBuilder.Entity<Purpose>().ToTable("Purposes");
        modelBuilder.Entity<ShiftLogEntry>().ToTable("ShiftLogs");
        modelBuilder.Entity<CashOnHandEntry>().ToTable("CashOnHand");
        modelBuilder.Entity<CheckPayout>().ToTable("CheckPayouts");
        modelBuilder.Entity<PosSalesSummary>().ToTable("PosSalesSummaries");
        modelBuilder.Entity<PosSalesTenderLine>().ToTable("PosSalesTenderLines");
        modelBuilder.Entity<PosSalesHourlyLine>().ToTable("PosSalesHourlyLines");
        modelBuilder.Entity<PosSalesDepartmentLine>().ToTable("PosSalesDepartmentLines");
        modelBuilder.Entity<PurchaseInvoice>().ToTable("PurchaseInvoices");
        modelBuilder.Entity<PurchaseInvoiceLine>()
            .ToTable("PurchaseInvoiceLines", table => table.UseSqlOutputClause(false));
        modelBuilder.Entity<ProductCost>().ToTable("ProductCosts");
        modelBuilder.Entity<PriceAlert>().ToTable("PriceAlerts");
        modelBuilder.Entity<Employee>().ToTable("Employees");
        modelBuilder.Entity<EmployeeDocument>().ToTable("EmployeeDocuments");
        modelBuilder.Entity<ScheduleShift>().ToTable("ScheduleShifts");
        modelBuilder.Entity<EmployeePeriodHours>().ToTable("EmployeePeriodHours");
        modelBuilder.Entity<PayrollRun>().ToTable("PayrollRuns");
        modelBuilder.Entity<PayrollEntry>().ToTable("PayrollEntries");
        modelBuilder.Entity<PayrollAuditEntry>().ToTable("PayrollAuditEntries");
        modelBuilder.Entity<ScheduleNotification>().ToTable("ScheduleNotifications");

        // SQL Server handles DATE type natively - no converter needed

        // Defaults for StoreId so current UI (single store) continues to work without changes.
        modelBuilder.Entity<Vendor>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<Purpose>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<ShiftLogEntry>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<CashOnHandEntry>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<CheckPayout>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<PosSalesSummary>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<ProductCost>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<PriceAlert>().Property(x => x.StoreId).HasDefaultValue(1);

        // Helpful indexes
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Role);

        modelBuilder.Entity<ShiftLogEntry>().HasIndex(x => new { x.StoreId, x.Date });
        modelBuilder.Entity<ShiftLogEntry>().HasIndex(x => x.PosSalesSummaryId).IsUnique();
        modelBuilder.Entity<CashOnHandEntry>().HasIndex(x => new { x.StoreId, x.Date });
        modelBuilder.Entity<CheckPayout>().HasIndex(x => new { x.StoreId, x.Date });
        modelBuilder.Entity<PosSalesSummary>().HasIndex(x => new { x.StoreId, x.ReportFrom, x.ReportTo });
        modelBuilder.Entity<PosSalesSummary>().HasIndex(x => new { x.StoreId, x.SourceFileSha256 }).IsUnique();
        modelBuilder.Entity<PosSalesTenderLine>().HasIndex(x => x.PosSalesSummaryId);
        modelBuilder.Entity<PosSalesHourlyLine>().HasIndex(x => x.PosSalesSummaryId);
        modelBuilder.Entity<PosSalesDepartmentLine>().HasIndex(x => x.PosSalesSummaryId);

        modelBuilder.Entity<Vendor>().HasIndex(x => new { x.StoreId, x.Name }).IsUnique();
        modelBuilder.Entity<Purpose>().HasIndex(x => new { x.StoreId, x.Name }).IsUnique();

        modelBuilder.Entity<PurchaseInvoice>().HasIndex(x => new { x.StoreId, x.InvoiceDate });
        modelBuilder.Entity<PurchaseInvoiceLine>().HasIndex(x => x.PurchaseInvoiceId);
        modelBuilder.Entity<ProductCost>().HasIndex(x => new { x.StoreId, x.ProductKey }).IsUnique();
        modelBuilder.Entity<PriceAlert>().HasIndex(x => new { x.StoreId, x.IsRead, x.CreatedUtc });
        modelBuilder.Entity<Employee>().HasIndex(x => new { x.StoreId, x.EmployeeNumber }).IsUnique();
        modelBuilder.Entity<Employee>().HasIndex(x => new { x.StoreId, x.IsActive, x.LastName });
        modelBuilder.Entity<EmployeeDocument>().HasIndex(x => new { x.EmployeeId, x.DocumentType, x.CreatedUtc });
        modelBuilder.Entity<ScheduleShift>().HasIndex(x => new { x.StoreId, x.ShiftDate, x.EmployeeId });
        modelBuilder.Entity<EmployeePeriodHours>().HasIndex(x => new { x.StoreId, x.EmployeeId, x.PeriodStart, x.PeriodEnd }).IsUnique();
        modelBuilder.Entity<PayrollRun>().HasIndex(x => new { x.StoreId, x.PeriodStart, x.PeriodEnd });
        modelBuilder.Entity<PayrollEntry>().HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique();
        modelBuilder.Entity<PayrollAuditEntry>().HasIndex(x => new { x.PayrollRunId, x.PerformedUtc });
        modelBuilder.Entity<ScheduleNotification>().HasIndex(x => new { x.StoreId, x.ScheduleFrom, x.EmployeeId });

        // Relationships - use NO ACTION for SQL Server to avoid cascade conflicts
        modelBuilder.Entity<CashOnHandEntry>()
            .HasOne(x => x.Vendor)
            .WithMany()
            .HasForeignKey(x => x.VendorId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<CashOnHandEntry>()
            .HasOne(x => x.Purpose)
            .WithMany()
            .HasForeignKey(x => x.PurposeId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<PurchaseInvoice>()
            .HasMany(x => x.Lines)
            .WithOne(x => x.PurchaseInvoice)
            .HasForeignKey(x => x.PurchaseInvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PurchaseInvoice>()
            .HasOne(x => x.Vendor)
            .WithMany()
            .HasForeignKey(x => x.VendorId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<PriceAlert>()
            .HasOne(x => x.PurchaseInvoice)
            .WithMany()
            .HasForeignKey(x => x.PurchaseInvoiceId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<PosSalesSummary>()
            .HasMany(x => x.TenderLines)
            .WithOne(x => x.PosSalesSummary)
            .HasForeignKey(x => x.PosSalesSummaryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PosSalesSummary>()
            .HasMany(x => x.HourlyLines)
            .WithOne(x => x.PosSalesSummary)
            .HasForeignKey(x => x.PosSalesSummaryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PosSalesSummary>()
            .HasMany(x => x.DepartmentLines)
            .WithOne(x => x.PosSalesSummary)
            .HasForeignKey(x => x.PosSalesSummaryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EmployeeDocument>()
            .HasOne(x => x.Employee)
            .WithMany()
            .HasForeignKey(x => x.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ScheduleShift>()
            .HasOne(x => x.Employee)
            .WithMany()
            .HasForeignKey(x => x.EmployeeId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<EmployeePeriodHours>()
            .HasOne(x => x.Employee)
            .WithMany()
            .HasForeignKey(x => x.EmployeeId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<PayrollRun>()
            .HasMany(x => x.Entries)
            .WithOne(x => x.PayrollRun)
            .HasForeignKey(x => x.PayrollRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
