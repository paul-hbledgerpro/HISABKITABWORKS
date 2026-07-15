using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ManagerPaperworkSystem.UI.Views;

public partial class LicenseActivationWindow : Window
{
    // ═══════════════════════════════════════════════════════════
    //  API URL — your deployed Azure API
    // ═══════════════════════════════════════════════════════════
    private const string API_BASE_URL = "https://hbstoreledger-api-dwfdg2hygggqhma3.canadacentral-01.azurewebsites.net";
    private const string OfflineLicensePublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA3akO1aSKySRsUf4XU9E/EcopIZEznVNKKHEiWSdE9BWfqDyrYtFrIPFjHC9TnHmECp5NDsn40vuR/ZVFgtohGw8P0b9ZceB84nfFeyCnAQvg1OiSHUuT8UK9CYffHssu7heBRHO/FJCXJZ4otqfenI9C6y3Jzr/wBhfQ9PHp4YraccWtoXc/U6PH7MbqWjc6p49ubdamn6lShxsbHv8X1sdf2lMDY4odBajn4rqrKV2VFdMC6T9rmLApPVlhzJ+6kgrHMe3ciwOF8EbfbvE7gq82ltPwWTviDBCxDK6OCjsx7jCt84TDvprv8I1c+8P1Qy9s2Orw0i3p4XXgWjsR7FWGf6L5x+xj8qxCUOyxP3/3NyFQBQTX+FTICCDZKafscWGbqT6OJV7skEaSBkThOMJuM+iMzSbQDb82CqjM3MIQFnsXgsP4aAjhCAFELVDj9u+tX1x1UJNxHZdB2eK1R2aLzrNVQQWaTSY2AulRmTEUH9XlK5BUf/5zpOVBDfURAgMBAAE=";

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab");

    private static readonly string ConnectionSettingsPath = Path.Combine(AppDataFolder, "connection_settings.json");
    private static readonly string LicenseFilePath = Path.Combine(AppDataFolder, "license.json");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public LicenseActivationWindow()
    {
        InitializeComponent();
        txtStoreName.Focus();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        // ── Gather inputs ──
        var storeName    = txtStoreName.Text.Trim();
        var storeAddress = txtStoreAddress.Text.Trim();
        var storeZip     = txtStoreZip.Text.Trim();
        var licenseKey   = txtLicenseKey.Text.Trim().ToUpper();

        // ── Validate ──
        if (string.IsNullOrEmpty(storeName))
        {
            ShowStatus("Please enter the store / business name.", "", isError: true);
            txtStoreName.Focus(); return;
        }
        if (string.IsNullOrEmpty(licenseKey))
        {
            ShowStatus("Please enter the license key.", "", isError: true);
            txtLicenseKey.Focus(); return;
        }
        if (!Regex.IsMatch(licenseKey, @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
        {
            ShowStatus("Invalid license key format.", "Expected: HBL-XXXX-XXXX-XXXX", isError: true);
            txtLicenseKey.Focus(); return;
        }

        SetBusy(true);
        ShowStatus("Validating license key...", "Contacting server...");

        try
        {
            // ── STEP 1: Call API to validate license and get DB connection info ──
            var requestBody = JsonSerializer.Serialize(new { licenseKey = licenseKey });
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync($"{API_BASE_URL}/api/license/activate", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var errorMsg = BuildActivationErrorMessage(response, responseJson);

                ShowStatus("Activation failed.", errorMsg, isError: true);
                return;
            }

            // ── STEP 2: Parse API response ──
            var data = JsonSerializer.Deserialize<JsonElement>(responseJson);

            var businessName = data.GetProperty("businessName").GetString() ?? storeName;
            var dbServer     = data.GetProperty("server").GetString() ?? "";
            var dbName       = data.GetProperty("database").GetString() ?? "";
            var dbUsername   = data.GetProperty("username").GetString() ?? "";
            var dbPassword   = data.GetProperty("password").GetString() ?? "";
            var maxStores    = data.GetProperty("maxStores").GetInt32();
            var maxUsers     = data.GetProperty("maxUsers").GetInt32();
            var expiresDate  = data.GetProperty("expiresDate").GetString() ?? "";

            if (string.IsNullOrEmpty(dbServer) || string.IsNullOrEmpty(dbName))
            {
                ShowStatus("Activation failed.", "Server returned incomplete database information.", isError: true);
                return;
            }

            ShowStatus("License valid!", $"{businessName} — validating database...");

            // ── STEP 3: Save connection_settings.json ──
            Directory.CreateDirectory(AppDataFolder);

            var connectionSettings = new
            {
                DatabaseType = "SqlServer",
                Server = dbServer,
                Database = dbName,
                Username = dbUsername,
                Password = dbPassword,
                ConnectionString = ""
            };

            File.WriteAllText(ConnectionSettingsPath,
                JsonSerializer.Serialize(connectionSettings, new JsonSerializerOptions { WriteIndented = true }));

            // ── STEP 4: Save license.json ──
            var licenseInfo = new
            {
                LicenseKey = licenseKey,
                BusinessName = businessName,
                DatabaseName = dbName,
                MaxStores = maxStores,
                MaxUsers = maxUsers,
                ExpiresDate = expiresDate,
                ActivatedAt = DateTime.UtcNow.ToString("o")
            };

            File.WriteAllText(LicenseFilePath,
                JsonSerializer.Serialize(licenseInfo, new JsonSerializerOptions { WriteIndented = true }));

            // ── STEP 5: Save pending store info for setup wizard ──
            var pendingPath = Path.Combine(AppDataFolder, "pending_store_info.json");
            File.WriteAllText(pendingPath,
                JsonSerializer.Serialize(new
                {
                    StoreName = storeName,
                    StoreAddress = storeAddress,
                    StoreZip = storeZip
                }, new JsonSerializerOptions { WriteIndented = true }));

            // ── STEP 6: Success! ──
            System.Windows.MessageBox.Show(
                $"License activated successfully!\n\n" +
                $"Store: {storeName}\n" +
                $"Key: {licenseKey}\n" +
                $"Database: {dbName}\n\n" +
                "The app will now restart to complete setup.",
                "Activation Successful",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (HttpRequestException httpEx)
        {
            ShowStatus("Cannot reach the activation server.",
                $"Please check your internet connection and try again.\n\n{httpEx.Message}",
                isError: true);
        }
        catch (TaskCanceledException)
        {
            ShowStatus("Connection timed out.",
                "The server did not respond in time. Please try again.",
                isError: true);
        }
        catch (Exception ex)
        {
            ShowStatus("Activation failed.", ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ImportOfflineLicense_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Offline License",
            Filter = "Hisab Kitab License (*.hblicense)|*.hblicense|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SetBusy(true);
            ShowStatus("Importing offline license...", Path.GetFileName(dialog.FileName));

            var payload = ReadAndVerifyOfflineLicense(dialog.FileName);
            SaveOfflineActivation(payload);

            System.Windows.MessageBox.Show(
                $"Offline license imported successfully!\n\n" +
                $"Business: {payload.BusinessName}\n" +
                $"Key: {payload.LicenseKey}\n" +
                $"Database: {payload.Database}\n\n" +
                "The app will now restart to complete setup.",
                "Activation Successful",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowStatus("Offline activation failed.", ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal static OfflineLicensePayload ReadAndVerifyOfflineLicense(string path)
    {
        var fileJson = File.ReadAllText(path);
        var licenseFile = JsonSerializer.Deserialize<OfflineLicenseFile>(fileJson)
            ?? throw new InvalidOperationException("The selected license file is not valid.");

        if (licenseFile.Version != 1)
            throw new InvalidOperationException("This license file version is not supported.");

        var payloadBytes = Convert.FromBase64String(licenseFile.Payload);
        var signatureBytes = Convert.FromBase64String(licenseFile.Signature);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OfflineLicensePublicKeyBase64), out _);
        var verified = rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!verified)
            throw new InvalidOperationException("The license file signature is invalid. The file may have been changed or was not created by the HB license generator.");

        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        var payload = JsonSerializer.Deserialize<OfflineLicensePayload>(payloadJson)
            ?? throw new InvalidOperationException("The license payload is missing.");

        if (!Regex.IsMatch(payload.LicenseKey, @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
            throw new InvalidOperationException("The license key format is invalid.");

        if (string.IsNullOrWhiteSpace(payload.BusinessName))
            throw new InvalidOperationException("The license is missing the business name.");

        if (string.IsNullOrWhiteSpace(payload.Server) || string.IsNullOrWhiteSpace(payload.Database))
            throw new InvalidOperationException("The license is missing SQL Server connection information.");

        if (!DateTime.TryParse(payload.ExpiresUtc, out var expiresUtc))
            throw new InvalidOperationException("The license expiration date is invalid.");

        if (expiresUtc.ToUniversalTime() < DateTime.UtcNow)
            throw new InvalidOperationException($"This license expired on {expiresUtc:MM/dd/yyyy}.");

        return payload;
    }

    private void SaveOfflineActivation(OfflineLicensePayload payload)
    {
        Directory.CreateDirectory(AppDataFolder);

        var connectionSettings = new
        {
            DatabaseType = "SqlServer",
            Server = payload.Server,
            Database = payload.Database,
            Username = payload.Username,
            Password = payload.Password,
            ConnectionString = ""
        };

        File.WriteAllText(ConnectionSettingsPath,
            JsonSerializer.Serialize(connectionSettings, new JsonSerializerOptions { WriteIndented = true }));

        var licenseInfo = new
        {
            LicenseKey = payload.LicenseKey,
            BusinessName = payload.BusinessName,
            DatabaseName = payload.Database,
            MaxStores = payload.MaxStores,
            MaxUsers = payload.MaxUsers,
            ExpiresDate = payload.ExpiresUtc,
            ActivatedAt = DateTime.UtcNow.ToString("o"),
            ActivationMode = "Offline"
        };

        File.WriteAllText(LicenseFilePath,
            JsonSerializer.Serialize(licenseInfo, new JsonSerializerOptions { WriteIndented = true }));

        var storeName = string.IsNullOrWhiteSpace(txtStoreName.Text) ? payload.BusinessName : txtStoreName.Text.Trim();
        var pendingPath = Path.Combine(AppDataFolder, "pending_store_info.json");
        File.WriteAllText(pendingPath,
            JsonSerializer.Serialize(new
            {
                StoreName = storeName,
                StoreAddress = txtStoreAddress.Text.Trim(),
                StoreZip = txtStoreZip.Text.Trim()
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string BuildActivationErrorMessage(HttpResponseMessage response, string responseText)
    {
        try
        {
            var errorData = JsonSerializer.Deserialize<JsonElement>(responseText);
            if (errorData.TryGetProperty("message", out var msgProp))
            {
                var message = msgProp.GetString();
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

        return $"License validation failed. Server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }

    private void SetBusy(bool busy)
    {
        btnActivate.IsEnabled     = !busy;
        btnCancel.IsEnabled       = !busy;
        txtStoreName.IsEnabled    = !busy;
        txtStoreAddress.IsEnabled = !busy;
        txtStoreZip.IsEnabled     = !busy;
        txtLicenseKey.IsEnabled   = !busy;
        progressBar.Visibility    = busy ? Visibility.Visible : Visibility.Collapsed;
        btnActivate.Content       = busy ? "ACTIVATING..." : "ACTIVATE LICENSE";
    }

    private void ShowStatus(string message, string detail, bool isError = false, bool isSuccess = false)
    {
        statusBorder.Visibility = Visibility.Visible;
        txtStatus.Text = message;
        txtStatusDetail.Text = detail;

        if (isError)
        {
            statusBorder.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D1A1A"));
            statusBorder.BorderBrush = FindResource("ErrorRed") as System.Windows.Media.SolidColorBrush;
            txtStatus.Foreground = FindResource("ErrorRed") as System.Windows.Media.SolidColorBrush;
        }
        else if (isSuccess)
        {
            statusBorder.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A2D1A"));
            statusBorder.BorderBrush = FindResource("SuccessGreen") as System.Windows.Media.SolidColorBrush;
            txtStatus.Foreground = FindResource("SuccessGreen") as System.Windows.Media.SolidColorBrush;
        }
        else
        {
            statusBorder.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1A1A24"));
            statusBorder.BorderBrush = FindResource("BorderBrush") as System.Windows.Media.SolidColorBrush;
            txtStatus.Foreground = FindResource("Gold") as System.Windows.Media.SolidColorBrush;
        }
    }
}

public sealed class OfflineLicenseFile
{
    public int Version { get; set; }
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class OfflineLicensePayload
{
    public string LicenseKey { get; set; } = "";
    public string BusinessName { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int MaxStores { get; set; }
    public int MaxUsers { get; set; }
    public string ExpiresUtc { get; set; } = "";
    public string IssuedUtc { get; set; } = "";
}
