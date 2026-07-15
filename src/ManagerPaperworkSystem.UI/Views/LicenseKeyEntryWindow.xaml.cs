using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Data.SqlClient;

namespace ManagerPaperworkSystem.UI.Views;

/// <summary>
/// Simplified license key entry window for EXISTING clients who already have
/// connection_settings.json. Only asks for the license key, validates it,
/// and saves license.json. Does NOT touch the database connection.
/// </summary>
public partial class LicenseKeyEntryWindow : Window
{
    // ── Same credentials as LicenseActivationWindow ──
    private const string DB_SERVER   = "hbstoreledger-server.database.windows.net";
    private const string DB_NAME     = "HBLedgerPro_License";
    private const string DB_USER     = "HBLedgerAdmin";
    private const string DB_PASSWORD = "YOUR_PASSWORD_HERE"; // ← Same password as your other windows

    private static string LicenseConnStr =>
        $"Server={DB_SERVER};Database={DB_NAME};User Id={DB_USER};Password={DB_PASSWORD};TrustServerCertificate=True;Encrypt=True;";

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HISAB KITAB");

    private static readonly string LicenseFilePath = Path.Combine(AppDataFolder, "license.json");

    public LicenseKeyEntryWindow()
    {
        InitializeComponent();
        txtLicenseKey.Focus();
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var licenseKey = txtLicenseKey.Text.Trim().ToUpper();

        // Validate format
        if (string.IsNullOrEmpty(licenseKey))
        {
            txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
            txtStatus.Text = "Please enter your license key.";
            txtLicenseKey.Focus();
            return;
        }

        if (!Regex.IsMatch(licenseKey, @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
        {
            txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
            txtStatus.Text = "Invalid format. Expected: HBL-XXXX-XXXX-XXXX";
            txtLicenseKey.Focus();
            return;
        }

        btnActivate.IsEnabled = false;
        btnActivate.Content = "Validating...";
        txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D4AF37"));
        txtStatus.Text = "Connecting to license server...";

        try
        {
            // Validate against the license database
            string? businessName = null;
            string? expiresDate = null;
            int maxStores = 1;
            int maxUsers = 3;
            bool isActive = false;

            using (var conn = new SqlConnection(LicenseConnStr))
            {
                conn.Open();

                using var cmd = new SqlCommand(
                    "SELECT BusinessName, ExpiresDate, MaxStores, MaxUsers, IsActive " +
                    "FROM Licenses WHERE LicenseKey = @key", conn);
                cmd.Parameters.AddWithValue("@key", licenseKey);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    businessName = reader["BusinessName"]?.ToString();
                    expiresDate = reader["ExpiresDate"]?.ToString();
                    maxStores = reader.GetInt32(reader.GetOrdinal("MaxStores"));
                    maxUsers = reader.GetInt32(reader.GetOrdinal("MaxUsers"));
                    isActive = reader.GetBoolean(reader.GetOrdinal("IsActive"));
                }
                else
                {
                    txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
                    txtStatus.Text = "License key not found. Please check and try again.";
                    btnActivate.IsEnabled = true;
                    btnActivate.Content = "✓  ACTIVATE LICENSE";
                    return;
                }
            }

            // Check if license is active and not expired
            if (!isActive)
            {
                txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
                txtStatus.Text = "This license has been deactivated. Please contact support.";
                btnActivate.IsEnabled = true;
                btnActivate.Content = "✓  ACTIVATE LICENSE";
                return;
            }

            if (DateTime.TryParse(expiresDate, out var expiry) && expiry < DateTime.Now)
            {
                txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
                txtStatus.Text = $"This license expired on {expiry:MM/dd/yyyy}. Please renew.";
                btnActivate.IsEnabled = true;
                btnActivate.Content = "✓  ACTIVATE LICENSE";
                return;
            }

            // ── Save license.json ──
            Directory.CreateDirectory(AppDataFolder);

            var licenseData = new
            {
                LicenseKey = licenseKey,
                BusinessName = businessName ?? "",
                ExpiresDate = expiresDate ?? "",
                MaxStores = maxStores,
                MaxUsers = maxUsers,
                IsActive = isActive,
                ActivatedUtc = DateTime.UtcNow.ToString("O")
            };

            var json = JsonSerializer.Serialize(licenseData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LicenseFilePath, json);

            txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#22C55E"));
            txtStatus.Text = $"✓ License activated for {businessName}!";

            // Success — close with DialogResult = true
            DialogResult = true;
            Close();
        }
        catch (SqlException sqlEx)
        {
            txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
            txtStatus.Text = $"Connection error: {sqlEx.Message}";
            btnActivate.IsEnabled = true;
            btnActivate.Content = "✓  ACTIVATE LICENSE";
        }
        catch (Exception ex)
        {
            txtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
            txtStatus.Text = $"Error: {ex.Message}";
            btnActivate.IsEnabled = true;
            btnActivate.Content = "✓  ACTIVATE LICENSE";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
