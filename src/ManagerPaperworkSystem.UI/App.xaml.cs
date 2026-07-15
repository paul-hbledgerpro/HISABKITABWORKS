using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.UI.Services;
using ManagerPaperworkSystem.UI.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ManagerPaperworkSystem;

// NOTE: The UI project enables WinForms (for the Windows ColorDialog). That adds a global using for
// System.Windows.Forms.Application, which makes the short name "System.Windows.Application" ambiguous in WPF files.
// Fully-qualify the WPF System.Windows.Application base type here.
public partial class App : System.Windows.Application
{
    private IHost? _host;
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("System.Windows.Application host not started.");

    private static readonly string CrashLogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab",
        "Logs");

    private static readonly string ConnectionSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab",
        "connection_settings.json");

    // ── NEW: License file path ──
    private static readonly string LicenseFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab",
        "license.json");

    private static void RestartApplication()
    {
        var appDir = AppContext.BaseDirectory;
        var exePath = Path.Combine(appDir, "HISAB KITAB.exe");

        if (!File.Exists(exePath))
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) &&
                !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                exePath = processPath;
            }
        }

        if (File.Exists(exePath))
        {
            WriteStartupMarker("Restarting app from: " + exePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
            {
                WorkingDirectory = appDir,
                UseShellExecute = true
            });
            return;
        }

        var dllPath = Path.Combine(appDir, "HISAB KITAB.dll");
        if (File.Exists(dllPath))
        {
            WriteStartupMarker("Restarting app via dotnet DLL: " + dllPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{dllPath}\"")
            {
                WorkingDirectory = appDir,
                UseShellExecute = false
            });
        }
    }

    private static string WriteCrashLog(Exception ex, string source)
    {
        try
        {
            Directory.CreateDirectory(CrashLogDirectory);
            var path = Path.Combine(CrashLogDirectory, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine("HISAB KITAB - Crash Log");
            sb.AppendLine($"Time:   {DateTime.Now:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"OS:     {Environment.OSVersion}");
            sb.AppendLine($"64-bit: {Environment.Is64BitProcess}");
            sb.AppendLine();
            sb.AppendLine(RedactSensitiveText(ex.ToString()));
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string RedactSensitiveText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var redacted = Regex.Replace(text, @"(?i)(Password|Pwd)\s*=\s*[^;,\r\n]*", "$1=******");
        redacted = Regex.Replace(redacted, @"(?i)(User\s*Id|UID)\s*=\s*[^;,\r\n]*", "$1=******");
        return redacted;
    }

    private static void WriteStartupMarker(string message)
    {
        try
        {
            Directory.CreateDirectory(CrashLogDirectory);
            var path = Path.Combine(CrashLogDirectory, "startup_last.txt");
            File.WriteAllText(path, $"{DateTime.Now:O}  {RedactSensitiveText(message)}");
        }
        catch
        {
            // ignore
        }
    }

    private static void MigrateLegacyLocalFiles()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var newDir = Path.Combine(baseDir, "Hisab Kitab");
            Directory.CreateDirectory(newDir);

            var legacyDirs = new[]
            {
                Path.Combine(baseDir, "Hisab Works"),
                Path.Combine(baseDir, "HB STORE LEDGER PRO"),
                Path.Combine(baseDir, "HB Store Ledger Pro"),
                Path.Combine(baseDir, "Manager Paperwork System")
            };

            foreach (var legacyDir in legacyDirs.Where(Directory.Exists))
            {
                CopyLegacyFile(legacyDir, newDir, "connection_settings.json");
                CopyLegacyFile(legacyDir, newDir, "license.json");
                CopyLegacyFile(legacyDir, newDir, "pending_store_info.json");
            }
        }
        catch
        {
            // Legacy migration should never block startup.
        }
    }

    private static void CopyLegacyFile(string legacyDir, string newDir, string fileName)
    {
        var source = Path.Combine(legacyDir, fileName);
        var dest = Path.Combine(newDir, fileName);
        if (File.Exists(source) && !File.Exists(dest))
            File.Copy(source, dest, overwrite: false);
    }

    /// <summary>
    /// Gets the connection string from saved settings or returns default SQLite connection
    /// </summary>
    private static (string connectionString, bool useSqlServer) GetConnectionSettings()
    {
        try
        {
            if (File.Exists(ConnectionSettingsPath))
            {
                var json = File.ReadAllText(ConnectionSettingsPath);
                var settings = JsonSerializer.Deserialize<DatabaseConnectionSettings>(json);

                if (settings != null && settings.DatabaseType?.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Use pre-built connection string if provided
                    if (!string.IsNullOrEmpty(settings.ConnectionString))
                    {
                        WriteStartupMarker($"Using SQL Server (pre-built connection string)");
                        return (settings.ConnectionString, true);
                    }
                    
                    // Otherwise build connection string from individual fields
                    if (!string.IsNullOrEmpty(settings.Server) && !string.IsNullOrEmpty(settings.Database))
                    {
                        var connStr = $"Server={settings.Server};Database={settings.Database};";
                        
                        if (!string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(settings.Password))
                        {
                            connStr += $"User Id={settings.Username};Password={settings.Password};";
                        }
                        else
                        {
                            connStr += "Integrated Security=True;";
                        }
                        
                        connStr += "TrustServerCertificate=True;";
                        
                        WriteStartupMarker($"Using SQL Server: {settings.Server}/{settings.Database}");
                        return (connStr, true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteStartupMarker($"Error reading connection settings: {ex.Message}");
        }

        // Default to SQLite
        WriteStartupMarker("Using SQLite (default)");
        return ("", false);
    }

    private void InstallCrashHandlers()
    {
        // UI thread exceptions
        DispatcherUnhandledException += (_, args) =>
        {
            var logPath = WriteCrashLog(args.Exception, "DispatcherUnhandledException");
            try
            {
                System.Windows.MessageBox.Show(
                    $"HISAB KITAB encountered an error and must close.\n\nCrash log saved to:\n{logPath}",
                    "HISAB KITAB",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* ignore */ }

            args.Handled = true;
            Shutdown();
        };

        // Background thread exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            WriteCrashLog(ex, "AppDomain.CurrentDomain.UnhandledException");
        };

        // Task exceptions
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
    }


    protected override async void OnStartup(StartupEventArgs e)
    {
        // IMPORTANT: if anything crashes during startup, we want a log and a visible message.
        InstallCrashHandlers();
        WriteStartupMarker("OnStartup begin");

        // Record architecture information for troubleshooting (e.g., Windows on ARM/Snapdragon).
        try
        {
            WriteStartupMarker($"Arch: Process={RuntimeInformation.ProcessArchitecture}, OS={RuntimeInformation.OSArchitecture}");
        }
        catch { /* ignore */ }

        // Diagnostics mode: verify the app can start and write logs even if the UI won't show.
        // Usage: "Hisab Kitab.exe" --diag
        if (e.Args.Any(a => string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Directory.CreateDirectory(CrashLogDirectory);
                var diagPath = Path.Combine(CrashLogDirectory, "diag_output.txt");
                var baseDir = AppContext.BaseDirectory;
                var files = Directory.Exists(baseDir)
                    ? Directory.GetFiles(baseDir, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)
                    : Enumerable.Empty<string>();

                var sb = new StringBuilder();
                sb.AppendLine("HISAB KITAB - Diagnostics");
                sb.AppendLine($"Time: {DateTime.Now:O}");
                sb.AppendLine($"BaseDir: {baseDir}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
                sb.AppendLine();
                sb.AppendLine("Top-level files:");
                foreach (var f in files) sb.AppendLine($"- {f}");
                File.WriteAllText(diagPath, sb.ToString());
                WriteStartupMarker("--diag completed");
            }
            catch
            {
                // ignore
            }
            Shutdown();
            return;
        }

        try
        {
            base.OnStartup(e);

            if (!Environment.Is64BitProcess)
            {
                System.Windows.MessageBox.Show("HISAB KITAB requires a 64-bit build on Windows.\n\nPlease reinstall the 64-bit version of the app.", "HISAB KITAB", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // NOTE: QuestPDF does NOT support Windows ARM64 (Snapdragon) at the time of this build.
            // Do NOT initialize QuestPDF during startup; initialize lazily only when generating PDFs.
            // This keeps the app usable on ARM64 even if PDF reporting is unavailable.

            MigrateLegacyLocalFiles();
            WriteStartupMarker("Legacy local settings migration checked");

            // Ensure SQLite native bundle is initialized (prevents startup crashes when native assets are missing/mismatched).
            try
            {
                SQLitePCL.Batteries_V2.Init();
                WriteStartupMarker("SQLitePCL init ok");
            }
            catch (Exception sqliteInitEx)
            {
                // Do not crash here; DB init will fail with a clearer exception if SQLite truly cannot load.
                WriteStartupMarker("SQLitePCL init failed: " + sqliteInitEx.Message);
            }

            // ============================================================
            // LICENSE CHECK — 3 scenarios:
            //
            // 1. FRESH INSTALL: No connection_settings.json AND no license.json
            //    → Show full License Activation (validates key + creates DB connection)
            //    → Saves both connection_settings.json and license.json → Restart
            //
            // 2. EXISTING CLIENT UPDATE: connection_settings.json EXISTS but no license.json
            //    → Show License Activation in "key-only" mode
            //    → Just validates key and saves license.json
            //    → Does NOT touch connection_settings.json (keeps existing DB)
            //    → Continues to login (no restart needed)
            //
            // 3. LICENSE EXISTS BUT NO CONNECTION: license.json exists, no connection_settings.json
            //    → Re-show full License Activation to re-create connection
            // ============================================================

            // Scenario 1: FRESH INSTALL — no files at all
            if (!File.Exists(ConnectionSettingsPath) && !File.Exists(LicenseFilePath))
            {
                WriteStartupMarker("No connection/license files found - showing License Activation");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var licenseWin = new LicenseActivationWindow();
                var activated = licenseWin.ShowDialog();

                if (activated != true)
                {
                    Shutdown();
                    return;
                }

                // License activation saved connection_settings.json
                // Now restart the app so it picks up the new database configuration
                WriteStartupMarker("License activated - restarting app");
                RestartApplication();
                Shutdown();
                return;
            }

            // Scenario 2: EXISTING CLIENT UPDATE — has DB connection but no license
            // This is the case when updating a client who was set up before licensing existed.
            // They already have their database configured and working — we just need a valid license key.
            // Uses a simple dedicated window (LicenseKeyEntryWindow) that only asks for the key.
            if (File.Exists(ConnectionSettingsPath) && !File.Exists(LicenseFilePath))
            {
                WriteStartupMarker("Existing client without license - showing LicenseKeyEntryWindow");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var keyEntryWin = new LicenseKeyEntryWindow();
                var activated = keyEntryWin.ShowDialog();

                if (activated != true)
                {
                    Shutdown();
                    return;
                }

                WriteStartupMarker("License key validated for existing client - continuing to login");
                // NO restart needed — connection_settings.json already exists
                // Fall through to normal startup below
            }

            // Scenario 3: LICENSE EXISTS BUT NO CONNECTION SETTINGS
            // (edge case: license.json exists but connection_settings.json was deleted)
            if (!File.Exists(ConnectionSettingsPath))
            {
                WriteStartupMarker("No connection_settings.json found - showing License Activation (re-activation)");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var licenseWin = new LicenseActivationWindow();
                var activated = licenseWin.ShowDialog();

                if (activated != true)
                {
                    Shutdown();
                    return;
                }

                WriteStartupMarker("Re-activation completed - restarting app");
                RestartApplication();
                Shutdown();
                return;
            }

            // Get connection settings (file exists at this point)
            var (connectionString, useSqlServer) = GetConnectionSettings();

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IAppPaths, AppPaths>();

                    if (useSqlServer && !string.IsNullOrEmpty(connectionString))
                    {
                        // Use SQL Server
                        services.AddPooledDbContextFactory<AppDbContext>(opts => {
                            opts.UseSqlServer(connectionString);
                        });

                        services.AddDbContext<AppDbContext>(opts => {
                            opts.UseSqlServer(connectionString);
                        }, ServiceLifetime.Transient);
                    }
                    else
                    {
                        // Use SQLite (default)
                        var paths = new AppPaths();
                        var sqliteConnectionString = $"Data Source={paths.DatabasePath}";
                        
                        services.AddPooledDbContextFactory<AppDbContext>(opts => {
                            opts.UseSqlite(sqliteConnectionString);
                        });

                        services.AddDbContext<AppDbContext>(opts => {
                            opts.UseSqlite(sqliteConnectionString);
                        }, ServiceLifetime.Transient);
                    }

                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<IAuthService, AuthService>();
                services.AddSingleton<IReportService, ReportService>();

                // UI preferences (theme/layout) stored in JSON (no DB migration required)
                services.AddSingleton<UiPreferencesService>();

                // POS report import (XLSX/PDF)
                services.AddSingleton<PosReportImportService>();

                services.AddSingleton<PurchaseService>();
                services.AddSingleton<InvoiceImportService>();

                services.AddSingleton<SessionState>();

                // Windows
                services.AddTransient<MainWindow>();
                services.AddTransient<LoginWindow>();
                services.AddTransient<SetupWizardWindow>();
                services.AddTransient<CreateAccountWindow>();
                services.AddTransient<UserAccountsWindow>();
                services.AddTransient<ChangePasswordWindow>();
                services.AddTransient<ResetPasswordWindow>();
                services.AddTransient<ReportsWindow>();
                services.AddTransient<ScreenSelectWindow>();
                services.AddTransient<StoreManagerWindow>();
                })
                .Build();

            WriteStartupMarker("Host built");
            await _host.StartAsync();
            WriteStartupMarker("Host started");

            // Theme is locked permanently to the HB black/gold brand.
            // Do NOT apply user-selected accents here (prevents unreadable fonts/colors).

            // Initialize DB
            WriteStartupMarker("DB init begin");
            using (var scope = _host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DbInitializer.InitializeAsync(db);
            }
            WriteStartupMarker("DB init done");

            // ============================================================
            // APPLY PENDING SETUP: If a fresh install just completed,
            // apply the store/admin data saved by the Setup Wizard.
            // ============================================================
            var pendingSetupPath = Path.Combine(
                Path.GetDirectoryName(ConnectionSettingsPath)!,
                "pending_setup.json");

            if (File.Exists(pendingSetupPath))
            {
                WriteStartupMarker("Applying pending setup data");
                try
                {
                    var pendingJson = File.ReadAllText(pendingSetupPath);
                    var pending = JsonSerializer.Deserialize<PendingSetupData>(pendingJson);

                    if (pending != null)
                    {
                        using var scope = _host.Services.CreateScope();
                        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                        var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                        var authSvc = scope.ServiceProvider.GetRequiredService<IAuthService>();

                        // Create store
                        using var db = dbFactory.CreateDbContext();
                        var store = db.Stores.FirstOrDefault();
                        if (store == null)
                        {
                            store = new ManagerPaperworkSystem.Core.Models.Store
                            {
                                Name = pending.StoreName,
                                Address = pending.StoreAddress,
                                IsActive = true,
                                CreatedUtc = DateTime.UtcNow
                            };
                            db.Stores.Add(store);
                            await db.SaveChangesAsync();
                        }
                        else
                        {
                            store.Name = pending.StoreName;
                            store.Address = pending.StoreAddress;
                            await db.SaveChangesAsync();
                        }

                        // Save app settings
                        var appSettings = await settingsSvc.GetSettingsAsync();
                        appSettings.StoreName = pending.StoreName;
                        appSettings.StoreAddress = pending.StoreAddress;
                        appSettings.DefaultReportType = (ManagerPaperworkSystem.Core.Models.ReportType)pending.DefaultReportType;
                        appSettings.DefaultStoreId = store.Id;
                        appSettings.LastStoreId = store.Id;
                        await settingsSvc.SaveSettingsAsync(appSettings);

                        // Create admin user
                        if (!await authSvc.HasAnyUsersAsync())
                        {
                            await authSvc.CreateUserAsync(
                                pending.FirstName, pending.LastName,
                                ManagerPaperworkSystem.Core.Models.UserRole.OwnerAdmin,
                                pending.Username, pending.Password,
                                pending.SecurityQuestion, pending.SecurityAnswer,
                                pending.Email ?? "");
                        }

                        WriteStartupMarker("Pending setup applied successfully");
                    }

                    // Delete the pending file
                    File.Delete(pendingSetupPath);
                }
                catch (Exception pendingEx)
                {
                    WriteStartupMarker($"Error applying pending setup: {pendingEx.Message}");
                    // Don't crash - let the app continue, user can re-setup via the wizard
                }
            }

            // ============================================================
            // APPLY PENDING STORE INFO: If license activation saved store info,
            // pre-populate the store name/address when setup wizard runs.
            // ============================================================
            var pendingStoreInfoPath = Path.Combine(
                Path.GetDirectoryName(ConnectionSettingsPath)!,
                "pending_store_info.json");

            if (File.Exists(pendingStoreInfoPath))
            {
                WriteStartupMarker("Found pending store info from license activation");
                try
                {
                    // The SetupWizardWindow can read this file to pre-fill store name/address
                    // We'll pass it through environment or the wizard can check for this file
                    var storeInfoJson = File.ReadAllText(pendingStoreInfoPath);
                    var storeInfo = JsonSerializer.Deserialize<JsonElement>(storeInfoJson);

                    // Store as environment vars for the setup wizard to pick up
                    if (storeInfo.TryGetProperty("StoreName", out var nameProp))
                        Environment.SetEnvironmentVariable("HB_PENDING_STORE_NAME", nameProp.GetString());
                    if (storeInfo.TryGetProperty("StoreAddress", out var addrProp))
                        Environment.SetEnvironmentVariable("HB_PENDING_STORE_ADDRESS", addrProp.GetString());

                    // Don't delete yet - setup wizard will delete after using it
                }
                catch (Exception storeInfoEx)
                {
                    WriteStartupMarker($"Error reading pending store info: {storeInfoEx.Message}");
                }
            }

            await RunFirstTimeFlowAsync();
        }
        catch (Exception ex)
        {
            var logPath = WriteCrashLog(ex, "OnStartup");
            try
            {
                var markerPath = Path.Combine(CrashLogDirectory, "startup_last.txt");
                var detail = RedactSensitiveText($"{ex.GetType().Name}: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"HISAB KITAB failed to start.\n\n{detail}\n\nStartup marker:\n{markerPath}\n\nCrash log saved to:\n{logPath}",
                    "HISAB KITAB",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* ignore */ }

            Shutdown();
        }
    }

    private async Task RunFirstTimeFlowAsync()
    {
        if (_host is null)
            return;

        // IMPORTANT:
        // When the first UI shown is a modal dialog (ShowDialog), WPF's default ShutdownMode
        // (OnLastWindowClose) can terminate the process as soon as the dialog closes (because
        // no other window exists yet). We switch to explicit shutdown for the login/setup flow,
        // and switch back once MainWindow is shown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool configured;
        bool hasUsers;

        using (var scope = _host.Services.CreateScope())
        {
            var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var authSvc = scope.ServiceProvider.GetRequiredService<IAuthService>();

            configured = await settingsSvc.IsConfiguredAsync();
            hasUsers = await authSvc.HasAnyUsersAsync();
        }

        if (!configured || !hasUsers)
        {
            var setupWin = _host.Services.GetRequiredService<SetupWizardWindow>();
            var ok = setupWin.ShowDialog();
            if (ok != true)
            {
                Shutdown();
                return;
            }
        }

        var loginWin = _host.Services.GetRequiredService<LoginWindow>();
        var logged = loginWin.ShowDialog();
        if (logged != true)
        {
            Shutdown();
            return;
        }

        var main = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        main.Activate();

        // Some machines don't paint the window frame/menu until the first resize.
        // Force an initial layout + non-client refresh after first render.
        _ = main.Dispatcher.InvokeAsync(() =>
        {
            main.InvalidateVisual();
            main.UpdateLayout();
            UI.Utils.WindowFrameRefresh.Refresh(main);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
        finally
        {
            base.OnExit(e);
        }
    }
}

/// <summary>
/// Settings for database connection stored in JSON file
/// </summary>
public class DatabaseConnectionSettings
{
    public string DatabaseType { get; set; } = "SQLite";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}
