using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Collections.ObjectModel;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.UI.Views;

public partial class StoreManagerWindow : Window
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsService _settingsService;
    private readonly SessionState _session;
    private readonly StoreConnectionService? _storeConnService;
    private readonly ObservableCollection<Store> _stores = new();

    // Path to store connections JSON file
    private static readonly string StoreConnectionsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab",
        "store_connections.json");

    public StoreManagerWindow(IDbContextFactory<AppDbContext> dbFactory, ISettingsService settingsService, SessionState session)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _settingsService = settingsService;
        _session = session;
        _storeConnService = null; // Will be set via SetStoreConnectionService
        Loaded += StoreManagerWindow_Loaded;
    }

    /// <summary>
    /// Optional: Set the StoreConnectionService for multi-database support.
    /// Called by MainWindow before showing the dialog.
    /// </summary>
    public void SetStoreConnectionService(StoreConnectionService service)
    {
        // Store in a field that can be accessed - using a workaround since constructor DI is complex
        _storeConnServiceField = service;
    }
    private StoreConnectionService? _storeConnServiceField;

    private async void StoreManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_session.IsAdmin)
            {
                System.Windows.MessageBox.Show(this, "Only the Owner/Admin can manage stores.", "Stores",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }
            await LoadStoresAsync();
            lblStatus.Text = $"{_stores.Count} store(s) configured. Click 'Add New Store' to connect a new store.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Stores", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadStoresAsync()
    {
        using var db = _dbFactory.CreateDbContext();
        var stores = await db.Stores.OrderBy(x => x.Id).ToListAsync();
        _stores.Clear();
        foreach (var s in stores) _stores.Add(s);
        gridStores.ItemsSource = _stores;
    }

    private async void AddNewStore_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new AddStoreWizard { Owner = this };
        if (wizard.ShowDialog() == true && wizard.ResultStore != null)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                db.Stores.Add(wizard.ResultStore);
                await db.SaveChangesAsync();
                _stores.Add(wizard.ResultStore);

                // Save the connection string for this store
                if (!string.IsNullOrEmpty(wizard.ResultConnectionString))
                {
                    SaveStoreConnection(wizard.ResultStore.Id, wizard.ResultConnectionString);

                    // Register with StoreConnectionService if available
                    _storeConnServiceField?.RegisterStore(wizard.ResultStore.Id, wizard.ResultConnectionString);
                }

                lblStatus.Text = $"✅ Store '{wizard.ResultStore.Name}' added successfully!";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  STORE CONNECTION PERSISTENCE
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Save a store's connection string to the local store_connections.json file.
    /// </summary>
    public static void SaveStoreConnection(int storeId, string connectionString)
    {
        try
        {
            var dir = Path.GetDirectoryName(StoreConnectionsPath)!;
            Directory.CreateDirectory(dir);

            var connections = LoadAllStoreConnections();
            connections[storeId.ToString()] = connectionString;

            var json = JsonSerializer.Serialize(connections, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoreConnectionsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving store connection: {ex.Message}");
        }
    }

    /// <summary>
    /// Load all store connections from the local JSON file.
    /// Returns a dictionary of StoreId -> ConnectionString.
    /// </summary>
    public static Dictionary<string, string> LoadAllStoreConnections()
    {
        try
        {
            if (File.Exists(StoreConnectionsPath))
            {
                var json = File.ReadAllText(StoreConnectionsPath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Remove a store's connection from the JSON file.
    /// </summary>
    public static void RemoveStoreConnection(int storeId)
    {
        try
        {
            var connections = LoadAllStoreConnections();
            connections.Remove(storeId.ToString());
            var json = JsonSerializer.Serialize(connections, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoreConnectionsPath, json);
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════
    //  SET DEFAULT / DELETE / SAVE / CLOSE
    // ════════════════════════════════════════════════════════════

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (gridStores.SelectedItem is not Store s) { lblStatus.Text = "Select a store first."; return; }
            var settings = await _settingsService.GetSettingsAsync();
            settings.DefaultStoreId = s.Id;
            settings.LastStoreId = s.Id;
            await _settingsService.SaveSettingsAsync(settings);
            lblStatus.Text = $"⭐ Default store set to: {s.Name}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Stores", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteStore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (gridStores.SelectedItem is not Store s) { lblStatus.Text = "Select a store first."; return; }
            if (_stores.Count <= 1)
            {
                System.Windows.MessageBox.Show(this, "Cannot delete the last store.", "Delete Store",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (System.Windows.MessageBox.Show(this,
                $"Delete '{s.Name}'?\n\nThis removes the store from this app.\nDatabase and transaction data will NOT be deleted.",
                "Delete Store", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            using var db = _dbFactory.CreateDbContext();
            var existing = await db.Stores.FindAsync(s.Id);
            if (existing != null) { db.Stores.Remove(existing); await db.SaveChangesAsync(); }
            _stores.Remove(s);

            // Remove stored connection
            RemoveStoreConnection(s.Id);
            _storeConnServiceField?.RegisterStore(s.Id, null);

            var settings = await _settingsService.GetSettingsAsync();
            if (settings.DefaultStoreId == s.Id || settings.LastStoreId == s.Id)
            {
                var first = _stores.FirstOrDefault();
                if (first != null) { settings.DefaultStoreId = first.Id; settings.LastStoreId = first.Id; await _settingsService.SaveSettingsAsync(settings); }
            }
            lblStatus.Text = $"🗑 Store '{s.Name}' deleted.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            foreach (var store in _stores.ToList())
            {
                var existing = await db.Stores.FindAsync(store.Id);
                if (existing != null) { existing.Name = store.Name; existing.Address = store.Address; existing.IsActive = store.IsActive; }
            }
            await db.SaveChangesAsync();
            lblStatus.Text = "💾 Changes saved.";
            Dispatcher.BeginInvoke(new Action(() => { try { DialogResult = true; } catch { } }));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { Close(); }
    }
}
