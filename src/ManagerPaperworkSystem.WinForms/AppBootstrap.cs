using System.Text.Json;
using System.Text.RegularExpressions;
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

    public static readonly string ConnectionSettingsPath = Path.Combine(AppDataDirectory, "connection_settings.json");
    public static readonly string LicenseFilePath = Path.Combine(AppDataDirectory, "license.json");

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
        var path = StoreConnectionsPath;
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static void SaveStoreConnections(Dictionary<string, string> connections)
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(StoreConnectionsPath, JsonSerializer.Serialize(connections, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ClearSavedConnectionSettings()
    {
        TryDelete(ConnectionSettingsPath);
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
            if (File.Exists(ConnectionSettingsPath))
            {
                var settings = JsonSerializer.Deserialize<DatabaseConnectionSettings>(File.ReadAllText(ConnectionSettingsPath));
                if (settings?.DatabaseType?.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                        return (settings.ConnectionString, true);

                    if (!string.IsNullOrWhiteSpace(settings.Server) && !string.IsNullOrWhiteSpace(settings.Database))
                    {
                        var conn = $"Server={settings.Server};Database={settings.Database};";
                        conn += !string.IsNullOrWhiteSpace(settings.Username)
                            ? $"User Id={settings.Username};Password={settings.Password};"
                            : "Integrated Security=True;";
                        conn += "TrustServerCertificate=True;Connect Timeout=30;ConnectRetryCount=2;ConnectRetryInterval=2;";
                        return (conn, true);
                    }
                }
            }
        }
        catch
        {
            // Fall back to SQLite. Startup should remain visible.
        }

        return ("", false);
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
