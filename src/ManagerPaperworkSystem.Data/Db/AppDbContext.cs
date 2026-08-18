using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ManagerPaperworkSystem.Data.Db;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<ActivityLogEntry> ActivityLogs => Set<ActivityLogEntry>();

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
        modelBuilder.Entity<ActivityLogEntry>().ToTable("ActivityLogs");
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
        modelBuilder.Entity<ActivityLogEntry>().HasIndex(x => new { x.StoreId, x.OccurredUtc });
        modelBuilder.Entity<ActivityLogEntry>().HasIndex(x => new { x.UserId, x.OccurredUtc });

        modelBuilder.Entity<ShiftLogEntry>().HasIndex(x => new { x.StoreId, x.Date });
        modelBuilder.Entity<ShiftLogEntry>().HasIndex(x => x.PosSalesSummaryId).IsUnique();
        modelBuilder.Entity<ShiftLogEntry>()
            .HasIndex(x => new { x.StoreId, x.PosReportKey })
            .IsUnique()
            .HasFilter("[PosReportKey] <> ''");
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

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        // Retaining entity states after a save is an advanced EF operation. A
        // second audit save would re-submit those same entries, so preserve the
        // caller's requested semantics and skip the supplemental audit write.
        if (!acceptAllChangesOnSuccess)
            return base.SaveChanges(false);
        var pending = CapturePendingActivity();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        PersistActivity(pending, acceptAllChangesOnSuccess);
        return result;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        if (!acceptAllChangesOnSuccess)
            return await base.SaveChangesAsync(false, cancellationToken);
        var pending = CapturePendingActivity();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await PersistActivityAsync(pending, acceptAllChangesOnSuccess, cancellationToken);
        return result;
    }

    private List<PendingActivity> CapturePendingActivity()
    {
        ChangeTracker.DetectChanges();
        var actor = ActivityAuditContext.Current;
        var pending = new List<PendingActivity>();
        foreach (var entry in ChangeTracker.Entries()
                     .Where(item => item.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is ActivityLogEntry || IsNoisyDetailEntity(entry.Entity))
                continue;

            var action = ActivityAction(entry);
            var changedFields = entry.State == EntityState.Modified
                ? entry.Properties
                    .Where(property => property.IsModified && !IsSensitiveProperty(property.Metadata.Name))
                    .Select(property => property.Metadata.Name)
                    .OrderBy(name => name)
                    .ToArray()
                : [];
            if (entry.State == EntityState.Modified && changedFields.Length == 0)
                continue;

            var storeId = ReadNullableInt(entry, "StoreId");
            var createdByUserId = ReadNullableInt(entry, "CreatedByUserId") ?? 0;
            var createdByName = ReadString(entry, "CreatedByName");
            var userId = actor?.UserId ?? createdByUserId;
            var userName = actor?.UserName;
            if (string.IsNullOrWhiteSpace(userName))
                userName = string.IsNullOrWhiteSpace(createdByName) ? "Automatic system" : createdByName;
            var isSystem = actor?.IsSystem ?? userId == 0;

            var log = new ActivityLogEntry
            {
                StoreId = storeId,
                UserId = userId,
                UserName = userName,
                UserRole = actor?.UserRole ?? (isSystem ? "System" : ""),
                Section = ActivitySection(entry.Entity),
                Action = action,
                EntityType = entry.Metadata.ClrType.Name,
                Description = ActivityDescription(entry, action, changedFields),
                IsSystem = isSystem,
                OccurredUtc = DateTime.UtcNow
            };
            pending.Add(new PendingActivity(entry, log));
        }
        return pending;
    }

    private void PersistActivity(
        IReadOnlyCollection<PendingActivity> pending,
        bool acceptAllChangesOnSuccess)
    {
        if (pending.Count == 0)
            return;
        foreach (var item in pending)
        {
            item.Log.EntityId = ReadPrimaryKey(item.Entry);
            ActivityLogs.Add(item.Log);
        }
        base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private async Task PersistActivityAsync(
        IReadOnlyCollection<PendingActivity> pending,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
            return;
        foreach (var item in pending)
        {
            item.Log.EntityId = ReadPrimaryKey(item.Entry);
            ActivityLogs.Add(item.Log);
        }
        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static bool IsNoisyDetailEntity(object entity) => entity is
        PosSalesTenderLine or PosSalesHourlyLine or PosSalesDepartmentLine or
        PurchaseInvoiceLine or PayrollEntry;

    private static bool IsSensitiveProperty(string name) =>
        name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Salt", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase);

    private static string ActivityAction(EntityEntry entry)
    {
        if (entry.State == EntityState.Added && entry.Entity is ShiftLogEntry { IsCorrection: true } shift)
            return shift.CorrectionReason.StartsWith("Owner/Admin undo", StringComparison.OrdinalIgnoreCase) ? "Undo" : "Correction";
        if (entry.State == EntityState.Added && entry.Entity is CashOnHandEntry { IsCorrection: true } cash)
            return cash.CorrectionReason.StartsWith("Owner/Admin undo", StringComparison.OrdinalIgnoreCase) ? "Undo" : "Correction";
        if (entry.State == EntityState.Added && entry.Entity is CheckPayout { IsCorrection: true } check)
            return check.CorrectionReason.StartsWith("Owner/Admin undo", StringComparison.OrdinalIgnoreCase) ? "Undo" : "Correction";
        return entry.State switch
        {
            EntityState.Added => "Created",
            EntityState.Modified => "Updated",
            EntityState.Deleted => "Deleted",
            _ => "Changed"
        };
    }

    private static string ActivitySection(object entity) => entity switch
    {
        ShiftLogEntry => "Shift Cash Drop",
        CashOnHandEntry => "Cash On Hand",
        CheckPayout => "Check Payout",
        PosSalesSummary => "Cash & Sales Summary",
        PurchaseInvoice => "Purchases",
        ProductCost => "Product Costs",
        PriceAlert => "Price Alerts",
        Vendor or Purpose => "Vendors & Purposes",
        Employee or EmployeeDocument => "Employees",
        ScheduleShift or ScheduleNotification => "Scheduling",
        PayrollRun or PayrollAuditEntry => "Payroll",
        Store => "Stores",
        UserAccount => "User Accounts",
        AppSettings => "Settings",
        _ => entity.GetType().Name
    };

    private static string ActivityDescription(
        EntityEntry entry,
        string action,
        IReadOnlyCollection<string> changedFields)
    {
        var label = ActivitySection(entry.Entity);
        if (entry.Entity is ShiftLogEntry shift && shift.IsCorrection)
            return $"Corrected Shift Cash Drop entry #{shift.CorrectsId}: {shift.CorrectionReason}".TrimEnd(':', ' ');
        if (entry.Entity is CashOnHandEntry cash && cash.IsCorrection)
            return $"Corrected Cash On Hand entry #{cash.CorrectsId}: {cash.CorrectionReason}".TrimEnd(':', ' ');
        if (entry.Entity is CheckPayout check && check.IsCorrection)
            return $"Corrected Check Payout entry #{check.CorrectsId}: {check.CorrectionReason}".TrimEnd(':', ' ');
        if (changedFields.Count > 0)
            return $"{action} {label}; changed {string.Join(", ", changedFields)}.";
        return $"{action} {label}.";
    }

    private static int ReadPrimaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()?.Properties.FirstOrDefault();
        return key is null ? 0 : Convert.ToInt32(entry.Property(key.Name).CurrentValue ?? 0);
    }

    private static int? ReadNullableInt(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null)
            return null;
        var value = entry.State == EntityState.Deleted
            ? entry.Property(propertyName).OriginalValue
            : entry.Property(propertyName).CurrentValue;
        return value is null ? null : Convert.ToInt32(value);
    }

    private static string ReadString(EntityEntry entry, string propertyName)
    {
        var property = entry.Metadata.FindProperty(propertyName);
        if (property is null)
            return "";
        var value = entry.State == EntityState.Deleted
            ? entry.Property(propertyName).OriginalValue
            : entry.Property(propertyName).CurrentValue;
        return value?.ToString() ?? "";
    }

    private sealed record PendingActivity(EntityEntry Entry, ActivityLogEntry Log);
}
