using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal static class LicensedBusinessService
{
    private const string FileName = "licensed-businesses.protected";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-LICENSED-BUSINESSES-V1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string ProtectedBusinessesPath => Path.Combine(AppBootstrap.AppDataPath, FileName);

    public static void SaveFromLicense(DeviceLicensePayloadV2 payload)
    {
        var businesses = new List<LicensedBusinessConnection>();
        if (payload.Businesses.Count > 0)
        {
            if (payload.Businesses.Count > payload.MaxStores)
                throw new InvalidOperationException("The signed business list exceeds the licensed business limit.");
            if (payload.Businesses.Count(x => x.IsPrimary) != 1)
                throw new InvalidOperationException("The device license must contain exactly one primary login business.");
            if (payload.Businesses.GroupBy(x => x.BusinessId).Any(x => x.Count() > 1) ||
                payload.Businesses.GroupBy(x => x.DatabaseName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                throw new InvalidOperationException("The device license contains duplicate business records.");

            foreach (var business in payload.Businesses)
            {
                if (string.IsNullOrWhiteSpace(business.BusinessName) || string.IsNullOrWhiteSpace(business.DatabaseName))
                    throw new InvalidOperationException("The device license contains an incomplete business record.");
                var settings = DeviceLicenseService.DecryptConnectionPayload(
                    business.EncryptedConnectionKey,
                    business.EncryptedConnection,
                    business.ConnectionNonce,
                    business.ConnectionTag);
                if (!string.Equals(settings.Database, business.DatabaseName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A signed business record does not match its encrypted database connection.");
                businesses.Add(new LicensedBusinessConnection
                {
                    BusinessId = business.BusinessId,
                    BusinessName = business.BusinessName.Trim(),
                    Address = business.Address.Trim(),
                    StoreGuid = string.IsNullOrWhiteSpace(business.StoreGuid)
                        ? business.DatabaseName.Trim()
                        : business.StoreGuid.Trim(),
                    DatabaseName = business.DatabaseName.Trim(),
                    InvoiceInboxApiUrl = business.InvoiceInboxApiUrl.Trim().TrimEnd('/'),
                    InvoiceInboxAddress = business.InvoiceInboxAddress.Trim().ToLowerInvariant(),
                    InvoiceInboxApiToken = business.InvoiceInboxApiToken.Trim(),
                    IsPrimary = business.IsPrimary,
                    Connection = settings
                });
            }
        }
        else
        {
            var primary = DeviceLicenseService.LoadProtectedConnection()
                ?? throw new InvalidOperationException("The device license does not contain a primary database connection.");
            businesses.Add(new LicensedBusinessConnection
            {
                BusinessId = 0,
                BusinessName = payload.BusinessName,
                DatabaseName = primary.Database,
                IsPrimary = true,
                Connection = primary
            });
        }

        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        var clear = JsonSerializer.SerializeToUtf8Bytes(businesses, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(ProtectedBusinessesPath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static IReadOnlyList<LicensedBusinessConnection> Load()
    {
        if (!File.Exists(ProtectedBusinessesPath))
            return Array.Empty<LicensedBusinessConnection>();
        var protectedBytes = File.ReadAllBytes(ProtectedBusinessesPath);
        var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<List<LicensedBusinessConnection>>(clear, JsonOptions)
                ?? new List<LicensedBusinessConnection>();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static async Task SynchronizeAsync(IServiceProvider services)
    {
        var businesses = Load();
        if (businesses.Count == 0)
            return;

        if (businesses.Count(x => x.IsPrimary) != 1)
            throw new InvalidOperationException("The licensed business directory does not contain exactly one primary business.");

        await UpgradeLicensedDatabasesAsync(businesses);

        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = factory.CreateDbContext();
        var existing = await db.Stores.ToListAsync();
        var matchedIds = new HashSet<int>();
        var connections = new Dictionary<string, string>();

        foreach (var licensed in businesses.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.BusinessName))
        {
            var store = existing.FirstOrDefault(x => NamesMatch(x.Name, licensed.BusinessName));
            if (store is null)
            {
                store = new Store { CreatedUtc = DateTime.UtcNow };
                db.Stores.Add(store);
                existing.Add(store);
            }
            store.Name = licensed.BusinessName;
            store.Address = licensed.Address;
            store.IsActive = true;
            await db.SaveChangesAsync();
            matchedIds.Add(store.Id);

            if (!licensed.IsPrimary)
                connections[store.Id.ToString()] = BuildConnectionString(licensed.Connection);
        }

        foreach (var unrelated in existing.Where(x => !matchedIds.Contains(x.Id)))
            unrelated.IsActive = false;
        await db.SaveChangesAsync();
        AppBootstrap.SaveStoreConnections(connections);
    }

    private static async Task UpgradeLicensedDatabasesAsync(IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        var upgraded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var licensed in businesses.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.BusinessName))
        {
            var connectionString = BuildConnectionString(licensed.Connection);
            var builder = new SqlConnectionStringBuilder(connectionString);
            var databaseKey = $"{builder.DataSource}|{builder.InitialCatalog}";
            if (!upgraded.Add(databaseKey))
                continue;

            try
            {
                await DatabaseSchemaService.EnsureSchemaAsync(connectionString, licensed.BusinessName);
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlServer(connectionString, sql => sql.CommandTimeout(30))
                    .Options;
                await using var db = new AppDbContext(options);
                await DbInitializer.InitializeAsync(db);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The database for '{licensed.BusinessName}' could not be upgraded to the current HISAB KITAB version. {ex.Message}",
                    ex);
            }
        }
    }

    private static bool NamesMatch(string? left, string? right)
    {
        static string Normalize(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && a == b;
    }

    private static string BuildConnectionString(DatabaseConnectionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
            return new SqlConnectionStringBuilder(settings.ConnectionString) { ConnectTimeout = 10, TrustServerCertificate = true }.ConnectionString;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server,
            InitialCatalog = settings.Database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 10,
            ConnectRetryCount = 2,
            ConnectRetryInterval = 2
        };
        if (string.IsNullOrWhiteSpace(settings.Username))
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = settings.Username;
            builder.Password = settings.Password;
        }
        return builder.ConnectionString;
    }
}

internal sealed class LicensedBusinessConnection
{
    public int BusinessId { get; set; }
    public string BusinessName { get; set; } = "";
    public string Address { get; set; } = "";
    public string StoreGuid { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string InvoiceInboxApiUrl { get; set; } = "";
    public string InvoiceInboxAddress { get; set; } = "";
    public string InvoiceInboxApiToken { get; set; } = "";
    public bool IsPrimary { get; set; }
    public DatabaseConnectionSettings Connection { get; set; } = new();
}
