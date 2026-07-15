using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Data.SqlClient;

namespace ManagerPaperworkSystem.UI.Views;

/// <summary>
/// Database settings window for regular users.
/// Only allows changing server address - credentials are preserved from initial setup.
/// </summary>
public partial class DatabaseSettingsWindow : Window
{
    private readonly string _settingsPath;
    private ConnectionSettings? _currentSettings;

    public DatabaseSettingsWindow()
    {
        InitializeComponent();

        // Settings file path
        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HISAB KITAB");
        Directory.CreateDirectory(appDataFolder);
        _settingsPath = Path.Combine(appDataFolder, "connection_settings.json");

        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _currentSettings = JsonSerializer.Deserialize<ConnectionSettings>(json);

                if (_currentSettings != null && _currentSettings.DatabaseType == "SqlServer")
                {
                    txtServer.Text = _currentSettings.Server ?? "";
                    txtCurrentConnection.Text = $"Connected to: {_currentSettings.Server} → {_currentSettings.Database}";
                }
                else
                {
                    txtCurrentConnection.Text = "SQLite (Local Database)";
                    txtServer.IsEnabled = false;
                    txtServer.Text = "Local database - no server needed";
                }
            }
            else
            {
                txtCurrentConnection.Text = "No connection configured. Please run initial setup.";
                txtServer.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            txtCurrentConnection.Text = $"Error loading settings: {ex.Message}";
        }
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSettings == null || _currentSettings.DatabaseType != "SqlServer")
        {
            txtConnectionStatus.Text = "SQLite - no test needed";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
            return;
        }

        if (string.IsNullOrWhiteSpace(txtServer.Text))
        {
            txtError.Text = "Please enter a server address.";
            return;
        }

        txtConnectionStatus.Text = "Testing...";
        txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Yellow;
        txtError.Text = "";

        try
        {
            // Build connection string using saved credentials but new server
            var connectionString = $"Server={txtServer.Text.Trim()};Database={_currentSettings.Database};User Id={_currentSettings.Username};Password={_currentSettings.Password};TrustServerCertificate=True;Connection Timeout=10;";

            using var connection = new SqlConnection(connectionString);
            connection.Open();

            using var cmd = new SqlCommand("SELECT DB_NAME()", connection);
            var dbName = cmd.ExecuteScalar()?.ToString();

            txtConnectionStatus.Text = $"✓ Connected to {dbName}!";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
        catch (SqlException ex)
        {
            txtConnectionStatus.Text = "✗ Connection Failed";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;

            if (ex.Number == 53 || ex.Number == -1)
            {
                txtError.Text = $"Cannot reach server '{txtServer.Text}'. Check that the server is running and accessible.";
            }
            else if (ex.Number == 18456)
            {
                txtError.Text = "Login failed. The saved credentials may be invalid for this server.";
            }
            else if (ex.Number == 4060)
            {
                txtError.Text = $"Database '{_currentSettings.Database}' does not exist on this server.";
            }
            else
            {
                txtError.Text = ex.Message;
            }
        }
        catch (Exception ex)
        {
            txtConnectionStatus.Text = "✗ Failed";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            txtError.Text = ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        txtError.Text = "";

        if (_currentSettings == null)
        {
            txtError.Text = "No connection settings found. Please run initial setup.";
            return;
        }

        if (_currentSettings.DatabaseType != "SqlServer")
        {
            // SQLite - nothing to change
            DialogResult = true;
            Close();
            return;
        }

        if (string.IsNullOrWhiteSpace(txtServer.Text))
        {
            txtError.Text = "Server address is required.";
            return;
        }

        try
        {
            // Test connection before saving
            var connectionString = $"Server={txtServer.Text.Trim()};Database={_currentSettings.Database};User Id={_currentSettings.Username};Password={_currentSettings.Password};TrustServerCertificate=True;Connection Timeout=10;";

            using var connection = new SqlConnection(connectionString);
            connection.Open();
        }
        catch (Exception ex)
        {
            txtError.Text = $"Cannot connect to server: {ex.Message}";
            return;
        }

        try
        {
            // Update settings with new server (keep other credentials)
            _currentSettings.Server = txtServer.Text.Trim();
            _currentSettings.ConnectionString = $"Server={_currentSettings.Server};Database={_currentSettings.Database};User Id={_currentSettings.Username};Password={_currentSettings.Password};TrustServerCertificate=True;";

            var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);

            // Ask to restart
            var result = System.Windows.MessageBox.Show(
                "Settings saved. The application needs to restart for changes to take effect.\n\nRestart now?",
                "Restart Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Process.Start(exePath);
                }
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            txtError.Text = $"Error saving settings: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
