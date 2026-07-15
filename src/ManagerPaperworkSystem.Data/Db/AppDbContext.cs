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

    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<PurchaseInvoiceLine> PurchaseInvoiceLines => Set<PurchaseInvoiceLine>();
    public DbSet<ProductCost> ProductCosts => Set<ProductCost>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();

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
        modelBuilder.Entity<PurchaseInvoice>().ToTable("PurchaseInvoices");
        modelBuilder.Entity<PurchaseInvoiceLine>().ToTable("PurchaseInvoiceLines");
        modelBuilder.Entity<ProductCost>().ToTable("ProductCosts");
        modelBuilder.Entity<PriceAlert>().ToTable("PriceAlerts");

        // SQL Server handles DATE type natively - no converter needed

        // Defaults for StoreId so current UI (single store) continues to work without changes.
        modelBuilder.Entity<Vendor>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<Purpose>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<ShiftLogEntry>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<CashOnHandEntry>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<CheckPayout>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<ProductCost>().Property(x => x.StoreId).HasDefaultValue(1);
        modelBuilder.Entity<PriceAlert>().Property(x => x.StoreId).HasDefaultValue(1);

        // Helpful indexes
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Role);

        modelBuilder.Entity<ShiftLogEntry>().HasIndex(x => new { x.StoreId, x.Date });
        modelBuilder.Entity<CashOnHandEntry>().HasIndex(x => new { x.StoreId, x.Date });
        modelBuilder.Entity<CheckPayout>().HasIndex(x => new { x.StoreId, x.Date });

        modelBuilder.Entity<Vendor>().HasIndex(x => new { x.StoreId, x.Name }).IsUnique();
        modelBuilder.Entity<Purpose>().HasIndex(x => new { x.StoreId, x.Name }).IsUnique();

        modelBuilder.Entity<PurchaseInvoice>().HasIndex(x => new { x.StoreId, x.InvoiceDate });
        modelBuilder.Entity<PurchaseInvoiceLine>().HasIndex(x => x.PurchaseInvoiceId);
        modelBuilder.Entity<ProductCost>().HasIndex(x => new { x.StoreId, x.ProductKey }).IsUnique();
        modelBuilder.Entity<PriceAlert>().HasIndex(x => new { x.StoreId, x.IsRead, x.CreatedUtc });

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
    }
}
