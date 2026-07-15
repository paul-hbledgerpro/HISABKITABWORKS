using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.UI.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace ManagerPaperworkSystem.UI.Views;

/// <summary>
/// Two-step login flow (like the web portal):
///   Step 1: Enter username + password
///   Step 2: If user exists in multiple stores → pick one; if only one → go straight to dashboard
/// </summary>
public partial class LoginWindow : Window
{
    private readonly IAuthService _authService;
    private readonly SessionState _session;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    // Stores the user matched in after Step 1, keyed by storeId
    private readonly List<MatchedStore> _matchedStores = new();

    public LoginWindow(IAuthService authService, SessionState session, IDbContextFactory<AppDbContext> dbFactory)
    {
        InitializeComponent();
        _authService = authService;
        _session = session;
        _dbFactory = dbFactory;
    }

    // ────────────────── helpers ──────────────────

    /// <summary>Holds info about a store where the user was found.</summary>
    public class MatchedStore
    {
        public int StoreId { get; set; }
        public string StoreName { get; set; } = "";
        public string StoreAddress { get; set; } = "";
        public string? ConnectionString { get; set; }   // null = default DB
        public UserAccount User { get; set; } = null!;
    }

    // ────────────────── Window lifecycle ──────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        txtUser.Focus();
    }

    // ────────────────── Step 1: Credentials ──────────────────

    private void User_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) pwd.Focus();
    }

    private void Pwd_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoLogin();
    }

    private void Login_Click(object sender, RoutedEventArgs e) => DoLogin();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { Close(); }
    }

    private async void DoLogin()
    {
        lblError.Text = "";
        var username = txtUser.Text?.Trim() ?? "";
        var password = pwd.Password ?? "";

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            lblError.Text = "Username and password are required.";
            return;
        }

        try
        {
            _matchedStores.Clear();

            var normalized = username.ToLowerInvariant();
            var savedConnections = LoadAllStoreConnections();

            // ── Check default database ──
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var user = await db.Users.FirstOrDefaultAsync(u =>
                    u.IsActive && u.Username.ToLower() == normalized);

                if (user != null && ManagerPaperworkSystem.Core.Utils.PasswordHasher.VerifyPassword(
                    password, user.PasswordHashBase64, user.SaltBase64))
                {
                    // Get store info from default DB
                    var stores = await db.Stores.AsNoTracking().ToListAsync();
                    foreach (var store in stores)
                    {
                        // Skip stores that have their own remote connection (they'll be checked separately)
                        if (savedConnections.ContainsKey(store.Id.ToString())) continue;

                        _matchedStores.Add(new MatchedStore
                        {
                            StoreId = store.Id,
                            StoreName = store.Name ?? "Store",
                            StoreAddress = store.Address ?? "",
                            ConnectionString = null, // default DB
                            User = user
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Default DB auth check: {ex.Message}");
            }

            // ── Check each remote store database ──
            foreach (var kvp in savedConnections)
            {
                if (!int.TryParse(kvp.Key, out var storeId)) continue;

                try
                {
                    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
                    optionsBuilder.UseSqlServer(kvp.Value);
                    using var remoteDb = new AppDbContext(optionsBuilder.Options);

                    var remoteUser = await remoteDb.Users.FirstOrDefaultAsync(u =>
                        u.IsActive && u.Username.ToLower() == normalized);

                    if (remoteUser != null && ManagerPaperworkSystem.Core.Utils.PasswordHasher.VerifyPassword(
                        password, remoteUser.PasswordHashBase64, remoteUser.SaltBase64))
                    {
                        var remoteStore = await remoteDb.Stores.AsNoTracking().FirstOrDefaultAsync();
                        _matchedStores.Add(new MatchedStore
                        {
                            StoreId = storeId,
                            StoreName = remoteStore?.Name ?? $"Store {storeId}",
                            StoreAddress = remoteStore?.Address ?? "",
                            ConnectionString = kvp.Value,
                            User = remoteUser
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Remote store {kvp.Key} auth check: {ex.Message}");
                }
            }

            // ── Evaluate results ──
            // Deduplicate: prefer remote connection over default DB, and remove name duplicates
            var deduped = new Dictionary<int, MatchedStore>();
            foreach (var ms in _matchedStores)
            {
                if (!deduped.ContainsKey(ms.StoreId))
                    deduped[ms.StoreId] = ms;
                else if (ms.ConnectionString != null && deduped[ms.StoreId].ConnectionString == null)
                    deduped[ms.StoreId] = ms; // prefer remote connection
            }
            _matchedStores.Clear();
            _matchedStores.AddRange(deduped.Values);

            if (_matchedStores.Count == 0)
            {
                lblError.Text = "Invalid username or password.";
                return;
            }

            if (_matchedStores.Count == 1)
            {
                // Single store — go straight to dashboard
                SelectStore(_matchedStores[0]);
                return;
            }

            // Multiple stores — show store selector (Step 2)
            var displayName = _matchedStores[0].User.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = _matchedStores[0].User.Username;
            lblWelcome.Text = $"Welcome, {displayName}!";

            storeList.ItemsSource = _matchedStores
                .OrderBy(s => s.StoreName)
                .ToList();

            panelCredentials.Visibility = Visibility.Collapsed;
            panelStoreSelect.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            lblError.Text = $"Error: {ex.Message}";
        }
    }

    // ────────────────── Step 2: Store Selection ──────────────────

    private void StoreSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is MatchedStore store)
        {
            SelectStore(store);
        }
    }

    private void BackToLogin_Click(object sender, RoutedEventArgs e)
    {
        panelStoreSelect.Visibility = Visibility.Collapsed;
        panelCredentials.Visibility = Visibility.Visible;
        _matchedStores.Clear();
        lblError.Text = "";
    }

    /// <summary>
    /// Finalize login: set session, wire AuthService to the selected store, close dialog.
    /// </summary>
    private void SelectStore(MatchedStore store)
    {
        var user = store.User;

        _session.UserId = user.Id;
        _session.Username = user.Username;
        _session.DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        _session.Role = user.Role;
        _session.LastStoreId = store.StoreId;
        _session.StoreName = store.StoreName;

        // Wire AuthService to the selected store's database
        if (_authService is ManagerPaperworkSystem.Data.Services.AuthService concreteAuth)
        {
            if (store.ConnectionString != null)
            {
                var connStr = store.ConnectionString;
                concreteAuth.SetStoreDbCreator(() =>
                {
                    var ob = new DbContextOptionsBuilder<AppDbContext>();
                    ob.UseSqlServer(connStr);
                    return new AppDbContext(ob.Options);
                });
            }
            else
            {
                // Default DB — clear any previous override
                concreteAuth.SetStoreDbCreator(null);
            }
        }

        // Update last login timestamp in the correct store DB
        try
        {
            if (store.ConnectionString != null)
            {
                var ob = new DbContextOptionsBuilder<AppDbContext>();
                ob.UseSqlServer(store.ConnectionString);
                using var storeDb = new AppDbContext(ob.Options);
                var dbUser = storeDb.Users.FirstOrDefault(u => u.Id == user.Id);
                if (dbUser != null) { dbUser.LastLoginUtc = DateTime.UtcNow; storeDb.SaveChanges(); }
            }
            else
            {
                using var db = _dbFactory.CreateDbContext();
                var dbUser = db.Users.FirstOrDefault(u => u.Id == user.Id);
                if (dbUser != null) { dbUser.LastLoginUtc = DateTime.UtcNow; db.SaveChanges(); }
            }
        }
        catch { /* non-critical */ }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { DialogResult = true; } catch { }
        }));
    }

    // ────────────────── Account management links ──────────────────

    private void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<CreateAccountWindow>();
        win.Owner = this;
        win.ShowDialog();
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<ResetPasswordWindow>();
        win.Owner = this;
        win.ShowDialog();
    }

    // ────────────────── Utilities ──────────────────

    private static Dictionary<string, string> LoadAllStoreConnections()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hisab Kitab", "store_connections.json");

            if (!File.Exists(path))
                return new Dictionary<string, string>();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}
