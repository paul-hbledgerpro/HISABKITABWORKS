using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using ManagerPaperworkSystem.Core.Models;
using Microsoft.Data.SqlClient;

namespace ManagerPaperworkSystem.UI.Views;

public partial class AddStoreWizard : Window
{
    // Connection info from Step 1
    private string _server = "";
    private string _username = "";
    private string _password = "";

    // Selected database from Step 2
    private string _selectedDatabase = "";

    // Result — the Store object to add
    public Store? ResultStore { get; private set; }

    // Result — the connection string for the new store's database
    public string? ResultConnectionString { get; private set; }

    // API base
    private const string API_BASE = "https://hbstoreledger-api-dwfdg2hygggqhma3.canadacentral-01.azurewebsites.net";

    public AddStoreWizard()
    {
        InitializeComponent();
    }

    // ════════════════════════════════════════════════════════════
    //  STEP 1: Connect to SQL Server
    // ════════════════════════════════════════════════════════════

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        _server = (txtSqlServer.Text ?? "").Trim();
        _username = (txtSqlUser.Text ?? "").Trim();
        _password = txtSqlPass.Password ?? "";

        if (string.IsNullOrEmpty(_server) || string.IsNullOrEmpty(_username))
        {
            lblStep1Status.Text = "Please enter server address and username.";
            lblStep1Status.Foreground = MakeBrush("#FFef4444");
            return;
        }

        btnConnect.IsEnabled = false;
        btnConnect.Content = "⏳ Connecting...";
        lblStep1Status.Text = "Connecting to SQL Server...";
        lblStep1Status.Foreground = MakeBrush("#FF8a8a9a");

        try
        {
            var connStr = $"Server={_server};User Id={_username};Password={_password};" +
                          "TrustServerCertificate=True;Encrypt=True;Connection Timeout=10;";

            // Connect and list databases
            var databases = new List<string>();
            using (var conn = new SqlConnection(connStr))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE 'HisabKitab_%' OR name LIKE 'HisabWorks_%' OR name LIKE 'HBStoreLedger_%' ORDER BY name", conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    databases.Add(reader.GetString(0));
                }
            }

            if (databases.Count == 0)
            {
                lblStep1Status.Text = "Connected, but no Hisab Kitab store databases found.\nSupported database names: HisabKitab_StoreName, HisabWorks_StoreName, or HBStoreLedger_StoreName.";
                lblStep1Status.Foreground = MakeBrush("#FFef4444");
                return;
            }

            // Success — populate Step 2 and switch
            lstDatabases.ItemsSource = databases;
            lstDatabases.SelectedIndex = 0;

            GoToStep(2);
            lblStep2Status.Text = $"Found {databases.Count} store database(s). Select one and click 'Select Database'.";
            lblStep2Status.Foreground = MakeBrush("#FF22c55e");
        }
        catch (SqlException sqlEx)
        {
            lblStep1Status.Text = $"Connection failed: {sqlEx.Message}";
            lblStep1Status.Foreground = MakeBrush("#FFef4444");
        }
        catch (Exception ex)
        {
            lblStep1Status.Text = $"Error: {ex.Message}";
            lblStep1Status.Foreground = MakeBrush("#FFef4444");
        }
        finally
        {
            btnConnect.IsEnabled = true;
            btnConnect.Content = "🔌 Connect to Server";
        }
    }

    // ════════════════════════════════════════════════════════════
    //  STEP 2: Select Database
    // ════════════════════════════════════════════════════════════

    private void SelectDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (lstDatabases.SelectedItem is not string dbName || string.IsNullOrEmpty(dbName))
        {
            lblStep2Status.Text = "Please select a database from the list.";
            lblStep2Status.Foreground = MakeBrush("#FFef4444");
            return;
        }

        _selectedDatabase = dbName;

        // Pre-fill store name from database name (remove supported app prefixes)
        var storeName = dbName;
        if (storeName.StartsWith("HisabKitab_", StringComparison.OrdinalIgnoreCase))
            storeName = storeName.Substring("HisabKitab_".Length);
        else if (storeName.StartsWith("HisabWorks_", StringComparison.OrdinalIgnoreCase))
            storeName = storeName.Substring("HisabWorks_".Length);
        else if (storeName.StartsWith("HBStoreLedger_", StringComparison.OrdinalIgnoreCase))
            storeName = storeName.Substring("HBStoreLedger_".Length);

        txtSelectedDb.Text = dbName;
        txtFinalStoreName.Text = storeName;

        GoToStep(3);
    }

    private void BackToStep1_Click(object sender, RoutedEventArgs e) => GoToStep(1);

    // ════════════════════════════════════════════════════════════
    //  STEP 3: License Activation
    // ════════════════════════════════════════════════════════════

    private async void ActivateAndAdd_Click(object sender, RoutedEventArgs e)
    {
        var storeName = (txtFinalStoreName.Text ?? "").Trim();
        var licenseKey = (txtFinalLicenseKey.Text ?? "").Trim().ToUpper();
        var address = (txtFinalAddress.Text ?? "").Trim();

        if (string.IsNullOrEmpty(storeName))
        {
            lblStep3Status.Text = "Store name is required.";
            lblStep3Status.Foreground = MakeBrush("#FFef4444");
            return;
        }

        if (string.IsNullOrEmpty(licenseKey))
        {
            lblStep3Status.Text = "License key is required.";
            lblStep3Status.Foreground = MakeBrush("#FFef4444");
            return;
        }

        btnActivateAndAdd.IsEnabled = false;
        btnActivateAndAdd.Content = "Selecting license file...";
        lblStep3Status.Text = "Select the downloaded .hblicense file for this store.";
        lblStep3Status.Foreground = MakeBrush("#FF8a8a9a");

        try
        {
            var offlineLicense = SelectOfflineLicense();
            if (offlineLicense is null)
            {
                lblStep3Status.Text = "Activation cancelled. Select the downloaded license file to add this store.";
                lblStep3Status.Foreground = MakeBrush("#FF8a8a9a");
                return;
            }

            if (!string.Equals(offlineLicense.LicenseKey, licenseKey, StringComparison.OrdinalIgnoreCase))
            {
                lblStep3Status.Text = "Selected license file does not match the entered license key.";
                lblStep3Status.Foreground = MakeBrush("#FFef4444");
                return;
            }

            if (!string.Equals(offlineLicense.Database, _selectedDatabase, StringComparison.OrdinalIgnoreCase))
            {
                var result = System.Windows.MessageBox.Show(this,
                    $"The license file is for database:\n{offlineLicense.Database}\n\n" +
                    $"You selected:\n{_selectedDatabase}\n\n" +
                    "Use the license file database and continue?",
                    "Database Mismatch",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    lblStep3Status.Text = "Activation cancelled. Select the matching store database/license file.";
                    lblStep3Status.Foreground = MakeBrush("#FFef4444");
                    return;
                }

                _selectedDatabase = offlineLicense.Database;
                txtSelectedDb.Text = offlineLicense.Database;
            }

            btnActivateAndAdd.Content = "Testing database...";
            lblStep3Status.Text = "Testing database connection...";
            var connStr = BuildConnectionString(offlineLicense.Server, offlineLicense.Database, offlineLicense.Username, offlineLicense.Password);
            try
            {
                using var testConn = new SqlConnection(connStr);
                await testConn.OpenAsync();
            }
            catch (Exception connEx)
            {
                var result = System.Windows.MessageBox.Show(this,
                    $"Database connection test failed:\n{connEx.Message}\n\n" +
                    "You can still save the store, but the app will only be able to open it when the SQL server is reachable.\n\n" +
                    "Add the store anyway?",
                    "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            // Save the connection string for this store
            ResultConnectionString = connStr;

            // Create the store result
            ResultStore = new Store
            {
                Name = storeName,
                Address = address,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow
            };

            lblStep3Status.Text = $"License verified. Store: {offlineLicense.BusinessName}, Expires: {offlineLicense.ExpiresUtc}";
            lblStep3Status.Foreground = MakeBrush("#FF22c55e");

            // Small delay so user can see the success message
            await Task.Delay(800);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            lblStep3Status.Text = $"Error: {ex.Message}";
            lblStep3Status.Foreground = MakeBrush("#FFef4444");
        }
        finally
        {
            btnActivateAndAdd.IsEnabled = true;
            btnActivateAndAdd.Content = "Select License File & Add Store";
        }
    }

    private OfflineLicensePayload? SelectOfflineLicense()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select HISAB KITAB License File",
            Filter = "HISAB KITAB License (*.hblicense)|*.hblicense|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return null;

        return LicenseActivationWindow.ReadAndVerifyOfflineLicense(dialog.FileName);
    }

    private static string BuildConnectionString(string server, string database, string username, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = username,
            Password = password,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 30,
            ConnectRetryCount = 2,
            ConnectRetryInterval = 2
        };

        return builder.ConnectionString;
    }

    private static string BuildActivationErrorMessage(HttpResponseMessage response, string responseText)
    {
        try
        {
            using var errDoc = JsonDocument.Parse(responseText);
            if (errDoc.RootElement.TryGetProperty("message", out var msg))
            {
                var message = msg.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                    return message;
            }
        }
        catch
        {
            // Non-JSON responses are handled below.
        }

        if ((int)response.StatusCode == 403 &&
            responseText.Contains("web app is stopped", StringComparison.OrdinalIgnoreCase))
        {
            return "The activation server is currently stopped in Azure. Start the HB Store Ledger activation web app, then try again.";
        }

        return $"Server error HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }

    private void BackToStep2_Click(object sender, RoutedEventArgs e) => GoToStep(2);

    // ════════════════════════════════════════════════════════════
    //  STEP NAVIGATION
    // ════════════════════════════════════════════════════════════

    private void GoToStep(int step)
    {
        panelStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        panelStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        panelStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        // Update step indicator
        step1Circle.Background = MakeBrush(step >= 1 ? "#FFD4AF37" : "#FF222230");
        step2Circle.Background = MakeBrush(step >= 2 ? "#FFD4AF37" : "#FF222230");
        step3Circle.Background = MakeBrush(step >= 3 ? "#FFD4AF37" : "#FF222230");

        step2Text.Foreground = MakeBrush(step >= 2 ? "#FF000000" : "#FF8a8a9a");
        step3Text.Foreground = MakeBrush(step >= 3 ? "#FF000000" : "#FF8a8a9a");
        step2Label.Foreground = MakeBrush(step >= 2 ? "#FFD4AF37" : "#FF8a8a9a");
        step3Label.Foreground = MakeBrush(step >= 3 ? "#FFD4AF37" : "#FF8a8a9a");

        // Update subtitle
        txtSubtitle.Text = step switch
        {
            1 => "Step 1: Connect to SQL Server",
            2 => "Step 2: Select Store Database",
            3 => "Step 3: Enter License Key & Activate",
            _ => ""
        };
    }

    // ════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════

    private static System.Windows.Media.SolidColorBrush MakeBrush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new System.Windows.Media.SolidColorBrush(color);
    }
}
