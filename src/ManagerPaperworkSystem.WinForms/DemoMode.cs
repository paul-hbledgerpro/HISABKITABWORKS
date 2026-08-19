using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Core.Utils;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal static class DemoRuntime
{
    public const string PrimaryStoreName = "HISAB KITAB DEMO MARKET";
    public const string SecondaryStoreName = "HISAB KITAB DEMO LIQUOR";
    public const string DemoUsername = "demo-admin";
    public const string DemoPassword = "demo123";

    public static bool IsEnabled { get; private set; }
    public static string CaptureDirectory { get; private set; } = "";
    public static string PresentationDirectory { get; private set; } = "";

    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab Demo");

    public static string DatabasePath => Path.Combine(AppDataDirectory, "hisab_kitab_demo.db");

    public static void Enable()
    {
        IsEnabled = true;
        Directory.CreateDirectory(AppDataDirectory);
    }

    public static void ConfigureCapture(string? directory)
    {
        CaptureDirectory = string.IsNullOrWhiteSpace(directory)
            ? ""
            : Path.GetFullPath(directory.Trim());
    }

    public static void ConfigurePresentation(string? directory)
    {
        PresentationDirectory = string.IsNullOrWhiteSpace(directory)
            ? ""
            : Path.GetFullPath(directory.Trim());
    }

    public static void ConfigureLicense()
    {
        LicenseRuntime.IsReadOnly = false;
        LicenseRuntime.CurrentLicense = new DeviceLicensePayloadV2
        {
            ActivationId = "DEMO-ACTIVATION",
            LicenseKey = "DEMO-NOT-FOR-PRODUCTION",
            CustomerId = -1,
            LicenseId = -1,
            BusinessName = PrimaryStoreName,
            StoreGuid = "IL_HISABKITABDEMO_TBC_60120",
            StoreZip = "60120",
            StoreState = "IL",
            BusinessType = "Retail Demo",
            AppVersion = AppUpdateStartupService.CurrentVersion.ToString(),
            DeviceId = "DEMO-DEVICE",
            DeviceName = "DEMO WORKSTATION",
            Status = "Demo",
            MaxDevices = 1,
            MaxStores = 2,
            MaxUsers = 10,
            EnabledServices = "Accounting,Payroll,Scheduling",
            PayrollState = "IL",
            IssuedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ExpiresUtc = DateTime.UtcNow.AddYears(10).ToString("O", CultureInfo.InvariantCulture)
        };
        LicenseRuntime.ConfigurePayrollStateForConnection(null);
    }
}

internal static class DemoHiddenDesktop
{
    private const uint GenericAll = 0x10000000;
    private static IntPtr _desktop;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(
        string desktop,
        IntPtr device,
        IntPtr deviceMode,
        uint flags,
        uint desiredAccess,
        IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    public static void Run(Action action)
    {
        var name = $"HisabKitabDemo_{Environment.ProcessId}_{Guid.NewGuid():N}";
        _desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (_desktop == IntPtr.Zero)
            throw new InvalidOperationException(
                $"The background demo desktop could not be created (Windows error {Marshal.GetLastWin32Error()}).");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(_desktop))
                    throw new InvalidOperationException(
                        $"The background demo thread could not attach to its desktop (Windows error {Marshal.GetLastWin32Error()}).");
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = false;
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("The background demo failed to run.", failure);
    }
}

internal sealed class DemoAppPaths : IAppPaths
{
    public string AppDataDirectory { get; } = DemoRuntime.AppDataDirectory;
    public string DatabasePath { get; } = DemoRuntime.DatabasePath;
    public string BackupsDirectory { get; } = Path.Combine(DemoRuntime.AppDataDirectory, "Backups");

    public DemoAppPaths()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}

internal static class DemoDataService
{
    private const string SeedMarker = "HISAB KITAB DEMO MARKET";
    private static readonly string[] EmployeeFirstNames = ["Avery", "Jordan", "Priya", "Marcus", "Sofia", "Daniel"];
    private static readonly string[] EmployeeLastNames = ["Reed", "Patel", "Williams", "Kim", "Garcia", "Brown"];

    public static async Task EnsureSeededAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Stores.AnyAsync(store => store.Name == SeedMarker))
            return;

        await ResetAndSeedAsync(db);
    }

    public static async Task ResetAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await ResetAndSeedAsync(db);
    }

    public static async Task ResetAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await ResetAsync(factory);
    }

    public static void ConfigureDemoSession(IServiceProvider services)
    {
        var session = services.GetRequiredService<SessionState>();
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = factory.CreateDbContext();
        var user = db.Users.AsNoTracking().Single(user => user.Username == DemoRuntime.DemoUsername);
        var store = db.Stores.AsNoTracking().Single(store => store.Name == DemoRuntime.PrimaryStoreName);
        session.UserId = user.Id;
        session.Username = user.Username;
        session.DisplayName = user.DisplayName;
        session.Role = UserRole.OwnerAdmin;
        session.LastStoreId = store.Id;
        session.StoreName = store.Name;
    }

    private static async Task ResetAndSeedAsync(AppDbContext db)
    {
        await ClearAsync(db);

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var (adminHash, adminSalt) = PasswordHasher.HashPassword(DemoRuntime.DemoPassword);
        var (answerHash, answerSalt) = PasswordHasher.HashPassword("demo");
        var admin = new UserAccount
        {
            FirstName = "Demo",
            LastName = "Owner",
            Username = DemoRuntime.DemoUsername,
            Email = "demo@hisabkitabworks.example",
            Role = UserRole.OwnerAdmin,
            PasswordHashBase64 = adminHash,
            SaltBase64 = adminSalt,
            SecurityQuestion = "What kind of account is this?",
            SecurityAnswerHashBase64 = answerHash,
            SecurityAnswerSaltBase64 = answerSalt,
            IsActive = true,
            CreatedUtc = now.AddMonths(-6),
            LastLoginUtc = now
        };
        var (managerHash, managerSalt) = PasswordHasher.HashPassword(DemoRuntime.DemoPassword);
        var manager = new UserAccount
        {
            FirstName = "Taylor",
            LastName = "Manager",
            Username = "demo-manager",
            Email = "manager@hisabkitabworks.example",
            Role = UserRole.Manager,
            PasswordHashBase64 = managerHash,
            SaltBase64 = managerSalt,
            SecurityQuestion = "What kind of account is this?",
            SecurityAnswerHashBase64 = answerHash,
            SecurityAnswerSaltBase64 = answerSalt,
            IsActive = true,
            CreatedUtc = now.AddMonths(-4),
            LastLoginUtc = now.AddDays(-1)
        };
        db.Users.AddRange(admin, manager);

        var market = new Store
        {
            Name = DemoRuntime.PrimaryStoreName,
            Address = "1250 Demo Avenue, Elgin, IL 60120",
            IsActive = true,
            CreatedUtc = now.AddYears(-2)
        };
        var liquor = new Store
        {
            Name = DemoRuntime.SecondaryStoreName,
            Address = "480 Sample Road, Schaumburg, IL 60193",
            IsActive = true,
            CreatedUtc = now.AddYears(-1)
        };
        db.Stores.AddRange(market, liquor);
        await db.SaveChangesAsync();

        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new AppSettings();
            db.Settings.Add(settings);
        }
        settings.StoreName = market.Name;
        settings.StoreAddress = market.Address;
        settings.DefaultReportType = ReportType.All;
        settings.DefaultStoreId = market.Id;
        settings.LastStoreId = market.Id;
        settings.AccountantEmail = "accountant@hisabkitabworks.example";
        settings.AutoEmailBankStatementOnFifth = false;

        await SeedStoreAsync(db, market, 0, today, admin);
        await SeedStoreAsync(db, liquor, 1, today, admin);
        await db.SaveChangesAsync();
        await SeedBankActivityAsync(db, market.Id, today, 0);
        await SeedBankActivityAsync(db, liquor.Id, today, 1);
    }

    private static async Task ClearAsync(AppDbContext db)
    {
        await EnsureDemoBankTablesAsync(db);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM BankStatementTransactions; DELETE FROM BankConnections;");

        db.PayrollAuditEntries.RemoveRange(db.PayrollAuditEntries);
        db.PayrollEntries.RemoveRange(db.PayrollEntries);
        db.PayrollRuns.RemoveRange(db.PayrollRuns);
        db.EmployeePeriodHours.RemoveRange(db.EmployeePeriodHours);
        db.ScheduleNotifications.RemoveRange(db.ScheduleNotifications);
        db.ScheduleShifts.RemoveRange(db.ScheduleShifts);
        db.EmployeeDocuments.RemoveRange(db.EmployeeDocuments);
        db.Employees.RemoveRange(db.Employees);
        db.PriceAlerts.RemoveRange(db.PriceAlerts);
        db.ProductCosts.RemoveRange(db.ProductCosts);
        db.PurchaseInvoiceLines.RemoveRange(db.PurchaseInvoiceLines);
        db.PurchaseInvoices.RemoveRange(db.PurchaseInvoices);
        db.ShiftLogs.RemoveRange(db.ShiftLogs);
        db.CashOnHand.RemoveRange(db.CashOnHand);
        db.CheckPayouts.RemoveRange(db.CheckPayouts);
        db.PosSalesTenderLines.RemoveRange(db.PosSalesTenderLines);
        db.PosSalesHourlyLines.RemoveRange(db.PosSalesHourlyLines);
        db.PosSalesDepartmentLines.RemoveRange(db.PosSalesDepartmentLines);
        db.PosSalesSummaries.RemoveRange(db.PosSalesSummaries);
        db.Vendors.RemoveRange(db.Vendors);
        db.Purposes.RemoveRange(db.Purposes);
        db.Users.RemoveRange(db.Users);
        db.Stores.RemoveRange(db.Stores);
        await db.SaveChangesAsync();
    }

    private static async Task SeedStoreAsync(
        AppDbContext db,
        Store store,
        int storeIndex,
        DateOnly today,
        UserAccount admin)
    {
        var vendorNames = storeIndex == 0
            ? new[] { "Midwest Wholesale", "Metro Beverage Supply", "City Utilities", "Premier Paper", "Fresh Snacks Distribution" }
            : new[] { "Heritage Beverage Group", "Lakeview Wine & Spirits", "City Utilities", "Premier Paper", "Regional Distributing" };
        var vendors = vendorNames.Select(name => new Vendor { StoreId = store.Id, Name = name }).ToList();
        var purposes = new[] { "Inventory", "Utilities", "Maintenance", "Bank Deposit", "Office Supplies", "Store Expense" }
            .Select(name => new Purpose { StoreId = store.Id, Name = name })
            .ToList();
        db.Vendors.AddRange(vendors);
        db.Purposes.AddRange(purposes);
        await db.SaveChangesAsync();

        var random = new Random(7411 + storeIndex * 101);
        var baseDailySales = storeIndex == 0 ? 3150m : 4650m;
        for (var offset = 59; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var weekendMultiplier = date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? 1.28m : 1m;
            var netSales = Money((baseDailySales + random.Next(-420, 620)) * weekendMultiplier);
            var taxes = Money(netSales * 0.0825m);
            var cashSales = Money(netSales * (0.31m + (decimal)random.NextDouble() * 0.08m));
            var cardSales = netSales - cashSales;
            var cashDrop = Money(cashSales + random.Next(-8, 9));
            var report = new PosSalesSummary
            {
                StoreId = store.Id,
                ReportFrom = date,
                ReportTo = date,
                SourceSystem = "AdventPOS Demo",
                ReportedStoreName = store.Name,
                SourceFileName = $"DEMO_CASH_SALES_{date:yyyyMMdd}.pdf",
                SourceFilePath = "",
                SourceFileSha256 = Hash($"summary|{store.Id}|{date:yyyy-MM-dd}"),
                TenderTransactionCount = random.Next(115, 235),
                GrossAmountReceived = netSales + taxes,
                GrossSales = netSales + taxes,
                Taxes = taxes,
                NetSales = netSales,
                TaxableSales = Money(netSales * 0.91m),
                NonTaxableSales = Money(netSales * 0.09m),
                CashSales = cashSales,
                CardSales = cardSales,
                CustomerTransactionCount = random.Next(105, 225),
                CustomerAverageSale = Money(netSales / random.Next(105, 225)),
                UserLoginCount = 4,
                DeleteVoidCount = random.Next(0, 4),
                NoSaleCount = random.Next(0, 3),
                VoidDeleteAmount = Money(random.Next(0, 55)),
                TotalDiscount = Money(random.Next(15, 95)),
                DepartmentQuantity = random.Next(160, 340),
                DepartmentSales = netSales,
                DepartmentCost = Money(netSales * 0.61m),
                DepartmentProfit = Money(netSales * 0.39m),
                DepartmentProfitPercent = 39m,
                CashDropReceived = cashDrop,
                RegisterPayout = 0,
                IsReconciled = offset > 1,
                ReconciledByUserId = offset > 1 ? admin.Id : null,
                ReconciledByName = offset > 1 ? admin.DisplayName : "",
                ReconciledUtc = offset > 1 ? date.ToDateTime(new TimeOnly(23, 0)).ToUniversalTime() : null,
                ImportedByUserId = admin.Id,
                ImportedByName = admin.DisplayName,
                ImportedUtc = date.ToDateTime(new TimeOnly(23, 10)).ToUniversalTime(),
                TenderLines =
                [
                    new PosSalesTenderLine { TenderType = "Cash", TransactionCount = random.Next(35, 75), Amount = cashSales },
                    new PosSalesTenderLine { TenderType = "Credit / Debit", TransactionCount = random.Next(75, 155), Amount = cardSales }
                ],
                HourlyLines =
                [
                    new PosSalesHourlyLine { TimePeriod = "6 AM - 11 AM", TransactionCount = 35, Amount = Money(netSales * 0.20m) },
                    new PosSalesHourlyLine { TimePeriod = "11 AM - 5 PM", TransactionCount = 78, Amount = Money(netSales * 0.43m) },
                    new PosSalesHourlyLine { TimePeriod = "5 PM - Close", TransactionCount = 67, Amount = Money(netSales * 0.37m) }
                ],
                DepartmentLines = DepartmentLines(netSales, storeIndex)
            };
            db.PosSalesSummaries.Add(report);

            var firstCash = Money(cashSales * 0.46m);
            var firstCard = Money(cardSales * 0.44m);
            var firstDrop = Money(firstCash + random.Next(-4, 5));
            db.ShiftLogs.AddRange(
                new ShiftLogEntry
                {
                    StoreId = store.Id, Date = date, Employee = EmployeeFirstNames[(offset + storeIndex) % EmployeeFirstNames.Length], ShiftNo = "Register 1",
                    CashTotal = firstCash, CardTotal = firstCard, NetSales = Money(netSales * 0.45m), Tax = Money(taxes * 0.45m), CashDropReceived = firstDrop,
                    PosReportKey = $"DEMO-Z-{store.Id}-{date:yyyyMMdd}-1", CreatedByUserId = admin.Id, CreatedByName = admin.DisplayName,
                    CreatedUtc = date.ToDateTime(new TimeOnly(16, 15)).ToUniversalTime()
                },
                new ShiftLogEntry
                {
                    StoreId = store.Id, Date = date, Employee = EmployeeFirstNames[(offset + storeIndex + 2) % EmployeeFirstNames.Length], ShiftNo = "Register 2",
                    CashTotal = cashSales - firstCash, CardTotal = cardSales - firstCard, NetSales = netSales - Money(netSales * 0.45m), Tax = taxes - Money(taxes * 0.45m),
                    CashDropReceived = cashDrop - firstDrop, PosReportKey = $"DEMO-Z-{store.Id}-{date:yyyyMMdd}-2", CreatedByUserId = admin.Id, CreatedByName = admin.DisplayName,
                    CreatedUtc = date.ToDateTime(new TimeOnly(23, 5)).ToUniversalTime()
                });

            if (offset <= 30)
            {
                db.CashOnHand.Add(new CashOnHandEntry
                {
                    StoreId = store.Id, Date = date, CashAdded = cashDrop, Reference = $"DROP-{date:MMdd}", Description = "Daily register cash drops",
                    CreatedByUserId = admin.Id, CreatedByName = admin.DisplayName, CreatedUtc = date.ToDateTime(new TimeOnly(23, 20)).ToUniversalTime()
                });
                if (date.DayOfWeek == DayOfWeek.Monday)
                {
                    db.CashOnHand.Add(new CashOnHandEntry
                    {
                        StoreId = store.Id, Date = date, IsPayout = true, PayoutAmount = Money(cashDrop * 4.8m), Reference = $"DEP-{date:MMdd}",
                        VendorId = vendors[0].Id, PurposeId = purposes.Single(item => item.Name == "Bank Deposit").Id,
                        Description = "Weekly operating account deposit", CreatedByUserId = admin.Id, CreatedByName = admin.DisplayName,
                        CreatedUtc = date.ToDateTime(new TimeOnly(10, 30)).ToUniversalTime()
                    });
                }
            }
        }

        for (var i = 0; i < 10; i++)
        {
            var date = today.AddDays(-(i * 5 + 2));
            db.CheckPayouts.Add(new CheckPayout
            {
                StoreId = store.Id, Date = date, VendorName = vendors[i % vendors.Count].Name,
                Description = i % 3 == 0 ? "Inventory invoice payment" : i % 3 == 1 ? "Utilities and services" : "Store operating expense",
                CheckAmount = Money(285m + i * 137m + storeIndex * 85m), CheckNumber = (4100 + storeIndex * 300 + i).ToString(CultureInfo.InvariantCulture),
                Cleared = i > 1, CreatedByUserId = admin.Id, CreatedByName = admin.DisplayName,
                CreatedUtc = date.ToDateTime(new TimeOnly(11, 15)).ToUniversalTime()
            });
        }

        await SeedPurchasesAsync(db, store, storeIndex, today, vendors, admin);
        await SeedEmployeesSchedulingAndPayrollAsync(db, store, storeIndex, today, admin);
    }

    private static List<PosSalesDepartmentLine> DepartmentLines(decimal sales, int storeIndex)
    {
        var departments = storeIndex == 0
            ? new[] { ("Tobacco", .34m), ("Beverages", .25m), ("Snacks", .19m), ("Accessories", .14m), ("Other", .08m) }
            : new[] { ("Spirits", .35m), ("Beer", .27m), ("Wine", .22m), ("Mixers", .10m), ("Other", .06m) };
        return departments.Select(item =>
        {
            var departmentSales = Money(sales * item.Item2);
            var cost = Money(departmentSales * 0.61m);
            return new PosSalesDepartmentLine
            {
                Department = item.Item1, Quantity = Math.Round(departmentSales / 14m, 2), Sales = departmentSales,
                Cost = cost, Profit = departmentSales - cost, ProfitPercent = 39m, SalesPercent = item.Item2 * 100m
            };
        }).ToList();
    }

    private static async Task SeedPurchasesAsync(
        AppDbContext db,
        Store store,
        int storeIndex,
        DateOnly today,
        IReadOnlyList<Vendor> vendors,
        UserAccount admin)
    {
        var products = storeIndex == 0
            ? new[]
            {
                ("Premium Cigarette Carton", "10010001", 61.25m), ("Energy Drink 12-Pack", "10010002", 22.80m),
                ("Bottled Water 24-Pack", "10010003", 7.40m), ("Assorted Chips Case", "10010004", 18.65m),
                ("Pocket Lighter Display", "10010005", 29.90m), ("Paper Receipt Rolls", "10010006", 16.25m)
            }
            : new[]
            {
                ("Premium Vodka 750ml", "20020001", 16.80m), ("Imported Beer 24-Pack", "20020002", 27.25m),
                ("Cabernet Sauvignon Case", "20020003", 86.40m), ("Craft Bourbon 750ml", "20020004", 31.50m),
                ("Cocktail Mix Case", "20020005", 24.75m), ("Paper Receipt Rolls", "20020006", 16.25m)
            };
        var latest = new Dictionary<string, (decimal Cost, DateOnly Date, string Vendor, string Invoice)>();
        PurchaseInvoice? latestInvoice = null;
        for (var i = 0; i < 18; i++)
        {
            var date = today.AddDays(-(i * 3 + 1));
            var vendor = vendors[i % Math.Min(3, vendors.Count)];
            var invoice = new PurchaseInvoice
            {
                StoreId = store.Id, VendorId = vendor.Id, VendorName = vendor.Name,
                InvoiceNumber = $"D{storeIndex + 1}-{date:MMdd}-{100 + i}", InvoiceDate = date,
                Notes = "Demo invoice imported from vendor PDF", CreatedByUserId = admin.Id,
                CreatedByName = admin.DisplayName, CreatedUtc = date.ToDateTime(new TimeOnly(9, 10)).ToUniversalTime()
            };
            decimal total = 0;
            for (var lineIndex = 0; lineIndex < 3; lineIndex++)
            {
                var product = products[(i + lineIndex) % products.Length];
                var quantity = 2m + (i + lineIndex) % 5;
                var cost = Money(product.Item3 * (1m + ((i % 4) - 1) * 0.0125m));
                var amount = Money(quantity * cost);
                invoice.Lines.Add(new PurchaseInvoiceLine
                {
                    ProductName = product.Item1, ItemCode = product.Item2, OrdQuantity = quantity, ShipQuantity = quantity,
                    Quantity = quantity, UnitCost = cost, Price = cost, Amount = amount
                });
                total += amount;
                if (!latest.TryGetValue(product.Item2, out var existing) || date > existing.Date)
                    latest[product.Item2] = (cost, date, vendor.Name, invoice.InvoiceNumber);
            }
            invoice.Total = Money(total);
            db.PurchaseInvoices.Add(invoice);
            if (i == 0)
                latestInvoice = invoice;
        }
        await db.SaveChangesAsync();

        foreach (var product in products)
        {
            var info = latest[product.Item2];
            db.ProductCosts.Add(new ProductCost
            {
                StoreId = store.Id, ProductKey = product.Item1.ToUpperInvariant(), ProductName = product.Item1, Sku = product.Item2,
                LastUnitCost = info.Cost, LastInvoiceDate = info.Date, LastVendorName = info.Vendor,
                LastInvoiceNumber = info.Invoice, UpdatedUtc = DateTime.UtcNow.AddDays(-1)
            });
        }
        for (var i = 0; i < 6; i++)
        {
            var product = products[i];
            var current = latest[product.Item2];
            var oldCost = Money(current.Cost * (i % 2 == 0 ? 0.94m : 1.06m));
            db.PriceAlerts.Add(new PriceAlert
            {
                StoreId = store.Id, ProductKey = product.Item1.ToUpperInvariant(), ProductName = product.Item1, Sku = product.Item2,
                OldUnitCost = oldCost, NewUnitCost = current.Cost,
                Direction = current.Cost >= oldCost ? PriceChangeDirection.Up : PriceChangeDirection.Down,
                AlertType = i == 4 ? PriceAlertType.CrossVendorPrice : PriceAlertType.PriceChange,
                VendorName = current.Vendor, OldVendorName = i == 4 ? vendors[1].Name : current.Vendor,
                OldInvoiceNumber = $"OLD-{200 + i}", InvoiceNumber = current.Invoice, InvoiceDate = current.Date,
                PurchaseInvoiceId = latestInvoice?.Id, IsRead = i >= 4,
                ReadUtc = i >= 4 ? DateTime.UtcNow.AddDays(-1) : null, CreatedUtc = DateTime.UtcNow.AddDays(-i)
            });
        }
    }

    private static async Task SeedEmployeesSchedulingAndPayrollAsync(
        AppDbContext db,
        Store store,
        int storeIndex,
        DateOnly today,
        UserAccount admin)
    {
        var employees = new List<Employee>();
        for (var i = 0; i < EmployeeFirstNames.Length; i++)
        {
            employees.Add(new Employee
            {
                StoreId = store.Id, EmployeeNumber = $"{storeIndex + 1}{i + 1:000}", FirstName = EmployeeFirstNames[i],
                LastName = EmployeeLastNames[i], Address = $"{210 + i * 17} Example Street", City = "Elgin", State = "IL", Zip = "60120",
                Phone = $"(847) 555-01{i + storeIndex * 10:00}", Email = $"{EmployeeFirstNames[i].ToLowerInvariant()}.{EmployeeLastNames[i].ToLowerInvariant()}@demo.example",
                SsnLast4 = (3100 + storeIndex * 100 + i * 17).ToString(CultureInfo.InvariantCulture),
                PayRate = 16.50m + i * 1.15m + storeIndex * .75m, PayType = EmployeePayType.Hourly,
                PayFrequency = PayFrequency.Biweekly, IsOvertimeEligible = true, WorkState = "IL", ResidenceState = "IL",
                HireDate = today.AddDays(-(220 + i * 55)), IsActive = true,
                FederalFilingStatus = i % 3 == 0 ? FederalFilingStatus.MarriedFilingJointly : FederalFilingStatus.SingleOrMarriedFilingSeparately,
                StateFilingStatus = i % 3 == 0 ? "Married" : "Single", StateAllowances = i % 2,
                W4OnFile = true, StateWithholdingOnFile = true, EmergencyContactName = $"Demo Contact {i + 1}",
                EmergencyContactPhone = $"(847) 555-02{i:00}", CreatedUtc = DateTime.UtcNow.AddMonths(-8), UpdatedUtc = DateTime.UtcNow.AddDays(-2)
            });
        }
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync();

        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        for (var day = 0; day < 14; day++)
        {
            var date = monday.AddDays(day);
            for (var employeeIndex = 0; employeeIndex < employees.Count; employeeIndex++)
            {
                if ((day + employeeIndex) % 6 == 0)
                    continue;
                var opening = (day + employeeIndex) % 2 == 0;
                db.ScheduleShifts.Add(new ScheduleShift
                {
                    StoreId = store.Id, EmployeeId = employees[employeeIndex].Id, ShiftDate = date,
                    StartTime = opening ? new TimeSpan(8, 0, 0) : new TimeSpan(14, 0, 0),
                    EndTime = opening ? new TimeSpan(16, 0, 0) : new TimeSpan(22, 0, 0),
                    UnpaidBreakMinutes = 30, Status = day < 7 ? ScheduleShiftStatus.Completed : ScheduleShiftStatus.Published,
                    Notes = opening ? "Opening shift" : "Closing shift", UpdatedByName = admin.DisplayName,
                    UpdatedUtc = DateTime.UtcNow.AddDays(-1)
                });
            }
        }

        for (var runIndex = 0; runIndex < 2; runIndex++)
        {
            var periodEnd = monday.AddDays(-1 - runIndex * 14);
            var periodStart = periodEnd.AddDays(-13);
            var payDate = periodEnd.AddDays(5);
            var run = new PayrollRun
            {
                StoreId = store.Id, PeriodStart = periodStart, PeriodEnd = periodEnd, PayDate = payDate,
                PayFrequency = PayFrequency.Biweekly, TaxYear = payDate.Year, Status = PayrollRunStatus.Finalized,
                TaxRuleSetId = "DEMO-IL-2026", TaxRuleVersion = "DEMO", TaxRuleSha256 = Hash("demo-tax-rules"),
                TaxRuleSources = "Demonstration calculations only — not for filing", TaxRulesVerifiedUtc = DateTime.UtcNow.AddDays(-8 - runIndex * 14),
                CreatedByName = admin.DisplayName, ApprovedByName = admin.DisplayName, FinalizedByName = admin.DisplayName,
                CreatedUtc = DateTime.UtcNow.AddDays(-8 - runIndex * 14), ApprovedUtc = DateTime.UtcNow.AddDays(-7 - runIndex * 14),
                FinalizedUtc = DateTime.UtcNow.AddDays(-7 - runIndex * 14)
            };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();

            foreach (var employee in employees)
            {
                var regularHours = 76m + employee.Id % 5;
                var overtimeHours = employee.Id % 3 == 0 ? 4m : 0m;
                var regularPay = Money(regularHours * employee.PayRate);
                var overtimePay = Money(overtimeHours * employee.PayRate * 1.5m);
                var gross = regularPay + overtimePay;
                var federal = Money(gross * .087m);
                var socialSecurity = Money(gross * .062m);
                var medicare = Money(gross * .0145m);
                var state = Money(gross * .0495m);
                db.EmployeePeriodHours.Add(new EmployeePeriodHours
                {
                    StoreId = store.Id, EmployeeId = employee.Id, PeriodStart = periodStart, PeriodEnd = periodEnd,
                    RegularHours = regularHours, OvertimeHours = overtimeHours, UpdatedByName = admin.DisplayName,
                    UpdatedUtc = DateTime.UtcNow.AddDays(-8 - runIndex * 14)
                });
                db.PayrollEntries.Add(new PayrollEntry
                {
                    PayrollRunId = run.Id, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                    PayRate = employee.PayRate, PayType = employee.PayType, ScheduledHours = regularHours,
                    RegularHours = regularHours, OvertimeHours = overtimeHours, RegularPay = regularPay, OvertimePay = overtimePay,
                    GrossPay = gross, FederalWithholding = federal, SocialSecurityWithholding = socialSecurity,
                    MedicareWithholding = medicare, StateWithholding = state, WorkState = "IL",
                    StateTaxRuleId = "DEMO-IL", StateTaxRuleVersion = "DEMO",
                    NetPay = gross - federal - socialSecurity - medicare - state,
                    GrossPayYtd = gross * (runIndex + 3), FederalWithholdingYtd = federal * (runIndex + 3),
                    SocialSecurityWithholdingYtd = socialSecurity * (runIndex + 3), MedicareWithholdingYtd = medicare * (runIndex + 3),
                    StateWithholdingYtd = state * (runIndex + 3), CheckNumber = $"P{storeIndex + 1}{runIndex + 1}{employee.Id:000}"
                });
            }
            db.PayrollAuditEntries.Add(new PayrollAuditEntry
            {
                StoreId = store.Id, PayrollRunId = run.Id, Action = "Finalized",
                Details = "Demo payroll reviewed and finalized.", PerformedByName = admin.DisplayName,
                PerformedUtc = DateTime.UtcNow.AddDays(-7 - runIndex * 14)
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsureDemoBankTablesAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS BankStatementTransactions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL DEFAULT 1,
    Date TEXT NOT NULL,
    Description TEXT NOT NULL DEFAULT '',
    Credit REAL NOT NULL DEFAULT 0,
    Debit REAL NOT NULL DEFAULT 0,
    CheckNumber TEXT,
    Category TEXT NOT NULL DEFAULT 'Other',
    ExternalTransactionId TEXT,
    Source TEXT NOT NULL DEFAULT 'Demo Bank',
    IsMatched INTEGER NOT NULL DEFAULT 0,
    MatchReference TEXT NOT NULL DEFAULT '',
    IncludeInProfitLoss INTEGER NOT NULL DEFAULT 0,
    CheckCopyPath TEXT NOT NULL DEFAULT '',
    ImportedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    CreatedByName TEXT NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_BankStatementTransactions_Store_External
    ON BankStatementTransactions(StoreId, ExternalTransactionId) WHERE ExternalTransactionId IS NOT NULL;
CREATE TABLE IF NOT EXISTS BankConnections (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL,
    ConnectionId TEXT NOT NULL,
    Provider TEXT NOT NULL DEFAULT '',
    InstitutionName TEXT NOT NULL DEFAULT '',
    AccountName TEXT NOT NULL DEFAULT '',
    AccountMask TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT '',
    LastSyncedUtc TEXT,
    LastError TEXT NOT NULL DEFAULT '',
    UpdatedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(StoreId, ConnectionId)
);");
    }

    private static async Task SeedBankActivityAsync(AppDbContext db, int storeId, DateOnly today, int storeIndex)
    {
        await EnsureDemoBankTablesAsync(db);
        await using var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await InsertAsync(connection,
            "INSERT INTO BankConnections (StoreId, ConnectionId, Provider, InstitutionName, AccountName, AccountMask, Status, LastSyncedUtc, LastError) VALUES (@sid,@id,'Demo','Community Business Bank','Operating Checking',@mask,'Active',@synced,'')",
            ("@sid", storeId), ("@id", $"demo-bank-{storeId}"), ("@mask", storeIndex == 0 ? "4821" : "7394"), ("@synced", DateTime.UtcNow.ToString("O")));

        var random = new Random(981 + storeIndex * 41);
        for (var offset = 40; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            if (date.DayOfWeek == DayOfWeek.Monday)
            {
                await InsertBankRowAsync(connection, storeId, date, "WEEKLY CASH DEPOSIT", 6850m + random.Next(-500, 650), 0, "", "Customer Payment", true, $"DEP-{date:MMdd}", false, $"deposit-{storeId}-{date:yyyyMMdd}");
            }
            if (offset % 4 == 0)
                await InsertBankRowAsync(connection, storeId, date, "WHOLESALE INVENTORY ACH", 0, 1250m + random.Next(50, 850), "", "Purchases", true, $"INV-{date:MMdd}", false, $"inventory-{storeId}-{date:yyyyMMdd}");
            if (offset % 9 == 0)
                await InsertBankRowAsync(connection, storeId, date, "CITY ELECTRIC AND GAS", 0, 410m + random.Next(15, 95), "", "Utilities", false, "", true, $"utility-{storeId}-{date:yyyyMMdd}");
        }
        await InsertBankRowAsync(connection, storeId, today.AddDays(-2), "CHECK 4108", 0, 742.35m, "4108", "Store Expense", true, "Check 4108", false, $"check-{storeId}-4108");
        await InsertBankRowAsync(connection, storeId, today.AddDays(-1), "MONTHLY BANK SERVICE FEE", 0, 24.00m, "", "Bank Charges", false, "", true, $"fee-{storeId}-{today:yyyyMM}");
    }

    private static Task InsertBankRowAsync(
        DbConnection connection, int storeId, DateOnly date, string description, decimal credit, decimal debit,
        string checkNumber, string category, bool matched, string reference, bool includeInProfitLoss, string externalId)
        => InsertAsync(connection,
            "INSERT INTO BankStatementTransactions (StoreId,Date,Description,Credit,Debit,CheckNumber,Category,ExternalTransactionId,Source,IsMatched,MatchReference,IncludeInProfitLoss,CreatedByName) VALUES (@sid,@date,@description,@credit,@debit,@check,@category,@external,'Demo Bank',@matched,@reference,@include,'Demo Owner')",
            ("@sid", storeId), ("@date", date.ToString("yyyy-MM-dd")), ("@description", description), ("@credit", credit), ("@debit", debit),
            ("@check", checkNumber), ("@category", category), ("@external", externalId), ("@matched", matched), ("@reference", reference), ("@include", includeInProfitLoss));

    private static async Task InsertAsync(DbConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var item in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = item.Name;
            parameter.Value = item.Value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
