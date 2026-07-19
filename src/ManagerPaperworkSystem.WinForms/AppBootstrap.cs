using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal static class AppBootstrap
{
    private static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab");
    private static readonly byte[] StoreConnectionEntropy = Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-STORE-CONNECTIONS-V1");

    public static readonly string ConnectionSettingsPath = Path.Combine(AppDataDirectory, "connection_settings.json");
    public static readonly string LicenseFilePath = Path.Combine(AppDataDirectory, "license.json");
    public static string AppDataPath => AppDataDirectory;

    public static ServiceProvider BuildServices()
    {
        Directory.CreateDirectory(AppDataDirectory);
        SQLitePCL.Batteries_V2.Init();

        var services = new ServiceCollection();
        var (connectionString, useSqlServer) = GetConnectionSettings();

        services.AddSingleton<IAppPaths, AppPaths>();

        if (useSqlServer && !string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddPooledDbContextFactory<AppDbContext>(opts => opts.UseSqlServer(connectionString));
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlServer(connectionString), ServiceLifetime.Transient);
        }
        else
        {
            var paths = new AppPaths();
            var sqliteConnection = $"Data Source={paths.DatabasePath}";
            services.AddPooledDbContextFactory<AppDbContext>(opts => opts.UseSqlite(sqliteConnection));
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(sqliteConnection), ServiceLifetime.Transient);
            connectionString = sqliteConnection;
        }

        services.AddSingleton(new ActiveConnectionInfo(connectionString, useSqlServer));
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<PurchaseService>();
        services.AddSingleton<InvoiceImportService>();
        services.AddSingleton<PosReportImportService>();
        services.AddSingleton<CheckPrintService>();
        services.AddSingleton<SessionState>();
        services.AddTransient<LoginForm>();
        services.AddTransient<MainForm>();
        services.AddTransient<SetupForm>();
        services.AddTransient<StoreManagerForm>();
        services.AddTransient<UserAccountsForm>();
        services.AddTransient<DatabaseSettingsForm>();
        services.AddTransient<CreateAccountForm>();
        services.AddTransient<ChangePasswordForm>();
        services.AddTransient<ResetPasswordForm>();
        services.AddTransient<PortalSyncSetupForm>();

        return services.BuildServiceProvider();
    }

    public static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<ActiveConnectionInfo>();
        if (connection.UseSqlServer && !string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            await DatabaseSchemaService.EnsureSchemaAsync(connection.ConnectionString);
        }

        using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await DbInitializer.InitializeAsync(db);
    }

    public static Dictionary<string, string> LoadStoreConnections()
    {
        try
        {
            if (File.Exists(StoreConnectionsProtectedPath))
            {
                var protectedBytes = File.ReadAllBytes(StoreConnectionsProtectedPath);
                var clear = ProtectedData.Unprotect(protectedBytes, StoreConnectionEntropy, DataProtectionScope.LocalMachine);
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                        ?? new Dictionary<string, string>();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }

            if (!File.Exists(StoreConnectionsPath))
                return new Dictionary<string, string>();

            var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StoreConnectionsPath))
                ?? new Dictionary<string, string>();
            SaveStoreConnections(legacy);
            return legacy;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static void SaveStoreConnections(Dictionary<string, string> connections)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(connections, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, StoreConnectionEntropy, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(StoreConnectionsProtectedPath, protectedBytes);
            TryDelete(StoreConnectionsPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static void ClearSavedConnectionSettings()
    {
        TryDelete(ConnectionSettingsPath);
        TryDelete(DeviceLicenseService.ProtectedConnectionPath);
        TryDelete(LicensedBusinessService.ProtectedBusinessesPath);
        TryDelete(StoreConnectionsPath);
        TryDelete(StoreConnectionsProtectedPath);
        TryDelete(Path.Combine(AppDataDirectory, "pending_store_info.json"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort only. If deletion fails, the repair form can still overwrite the file.
        }
    }

    public static string StoreConnectionsPath => Path.Combine(AppDataDirectory, "store_connections.json");
    public static string StoreConnectionsProtectedPath => Path.Combine(AppDataDirectory, "store_connections.protected");

    public static string RedactSensitiveText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var redacted = Regex.Replace(text, @"(?i)(Password|Pwd)\s*=\s*[^;,\r\n]*", "$1=******");
        return Regex.Replace(redacted, @"(?i)(User\s*Id|UID)\s*=\s*[^;,\r\n]*", "$1=******");
    }

    private static (string connectionString, bool useSqlServer) GetConnectionSettings()
    {
        try
        {
            var protectedSettings = DeviceLicenseService.LoadProtectedConnection();
            if (protectedSettings is not null)
                return BuildConnectionString(protectedSettings);

            if (File.Exists(ConnectionSettingsPath))
            {
                var settings = JsonSerializer.Deserialize<DatabaseConnectionSettings>(File.ReadAllText(ConnectionSettingsPath));
                if (settings is not null)
                    return BuildConnectionString(settings);
            }
        }
        catch
        {
            // Fall back to SQLite. Startup should remain visible.
        }

        return ("", false);
    }

    private static (string connectionString, bool useSqlServer) BuildConnectionString(DatabaseConnectionSettings settings)
    {
        if (!string.Equals(settings.DatabaseType, "SqlServer", StringComparison.OrdinalIgnoreCase))
            return ("", false);
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
            return (settings.ConnectionString, true);
        if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Database))
            return ("", false);

        var conn = $"Server={settings.Server};Database={settings.Database};";
        conn += !string.IsNullOrWhiteSpace(settings.Username)
            ? $"User Id={settings.Username};Password={settings.Password};"
            : "Integrated Security=True;";
        conn += "TrustServerCertificate=True;Connect Timeout=30;ConnectRetryCount=2;ConnectRetryInterval=2;";
        return (conn, true);
    }
}

internal sealed record ActiveConnectionInfo(string ConnectionString, bool UseSqlServer);

internal sealed class DatabaseConnectionSettings
{
    public string DatabaseType { get; set; } = "SQLite";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}
