using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class LicenseActivationForm : Form
{
    private const string ApiBaseUrl = "https://hbstoreledger-api-dwfdg2hygggqhma3.canadacentral-01.azurewebsites.net";
    private const string OfflineLicensePublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA3akO1aSKySRsUf4XU9E/EcopIZEznVNKKHEiWSdE9BWfqDyrYtFrIPFjHC9TnHmECp5NDsn40vuR/ZVFgtohGw8P0b9ZceB84nfFeyCnAQvg1OiSHUuT8UK9CYffHssu7heBRHO/FJCXJZ4otqfenI9C6y3Jzr/wBhfQ9PHp4YraccWtoXc/U6PH7MbqWjc6p49ubdamn6lShxsbHv8X1sdf2lMDY4odBajn4rqrKV2VFdMC6T9rmLApPVlhzJ+6kgrHMe3ciwOF8EbfbvE7gq82ltPwWTviDBCxDK6OCjsx7jCt84TDvprv8I1c+8P1Qy9s2Orw0i3p4XXgWjsR7FWGf6L5x+xj8qxCUOyxP3/3NyFQBQTX+FTICCDZKafscWGbqT6OJV7skEaSBkThOMJuM+iMzSbQDb82CqjM3MIQFnsXgsP4aAjhCAFELVDj9u+tX1x1UJNxHZdB2eK1R2aLzrNVQQWaTSY2AulRmTEUH9XlK5BUf/5zpOVBDfURAgMBAAE=";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly bool _keyOnly;
    private readonly TextBox _storeName = WinTheme.TextBox();
    private readonly TextBox _storeAddress = WinTheme.TextBox();
    private readonly TextBox _storeZip = WinTheme.TextBox();
    private readonly TextBox _licenseKey = WinTheme.TextBox();
    private readonly Label _status = WinTheme.Label("");
    private readonly Button _activate = WinTheme.Button("Activate License", true);
    private readonly Button _offline = WinTheme.Button("Import License File");
    private readonly Button _cancel = WinTheme.Button("Cancel");

    public LicenseActivationForm(bool keyOnly)
    {
        _keyOnly = keyOnly;
        WinTheme.Apply(this);
        Text = keyOnly ? "HISAB KITAB - License Key" : "HISAB KITAB - License Activation";
        Size = new Size(680, keyOnly ? 420 : 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        Controls.Add(Build());
        _activate.Click += async (_, _) => await ActivateOnlineAsync();
        _offline.Click += (_, _) => ImportOfflineLicense();
        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Shown += (_, _) => (_keyOnly ? _licenseKey : _storeName).Focus();
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(28), RowCount = 10, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        for (var i = 1; i < 8; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        root.Controls.Add(new Label
        {
            Text = _keyOnly ? "LICENSE KEY" : "LICENSE ACTIVATION",
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(22),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        if (!_keyOnly)
        {
            root.Controls.Add(WinTheme.Label("Store / Business Name *", true), 0, 1);
            root.Controls.Add(_storeName, 0, 2);
            root.Controls.Add(WinTheme.Label("Store Address", true), 0, 3);
            root.Controls.Add(_storeAddress, 0, 4);
            root.Controls.Add(WinTheme.Label("Store Zip Code", true), 0, 5);
            root.Controls.Add(_storeZip, 0, 6);
        }

        var licensePanel = new Panel { Dock = DockStyle.Fill, Height = 116, Padding = new Padding(0, 10, 0, 0) };
        var licenseLabel = WinTheme.Label("License Key *", true);
        licenseLabel.Dock = DockStyle.Top;
        _licenseKey.Dock = DockStyle.Top;
        licensePanel.Controls.Add(_licenseKey);
        licensePanel.Controls.Add(licenseLabel);
        root.Controls.Add(licensePanel, 0, _keyOnly ? 1 : 7);

        _status.ForeColor = WinTheme.Muted;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.Text = _keyOnly
            ? "Enter a license key or import the downloaded .hblicense file."
            : "Enter store information and license key, or import the downloaded .hblicense file.";
        root.Controls.Add(_status, 0, _keyOnly ? 2 : 8);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 58 };
        foreach (var b in new[] { _activate, _offline, _cancel })
        {
            b.Width = 170;
            actions.Controls.Add(b);
        }
        root.Controls.Add(actions, 0, 9);
        return root;
    }

    private async Task ActivateOnlineAsync()
    {
        var key = _licenseKey.Text.Trim().ToUpperInvariant();
        if (!ValidateKey(key))
            return;

        if (!_keyOnly && string.IsNullOrWhiteSpace(_storeName.Text))
        {
            SetStatus("Please enter the store / business name.", true);
            _storeName.Focus();
            return;
        }

        SetBusy(true);
        try
        {
            SetStatus("Validating license key with activation server...");
            var content = new StringContent(JsonSerializer.Serialize(new { licenseKey = key }), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync($"{ApiBaseUrl}/api/license/activate", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus(BuildActivationErrorMessage(response, responseJson), true);
                return;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(responseJson);
            var payload = new OfflineLicensePayload
            {
                LicenseKey = key,
                BusinessName = data.GetProperty("businessName").GetString() ?? _storeName.Text.Trim(),
                Server = data.GetProperty("server").GetString() ?? "",
                Database = data.GetProperty("database").GetString() ?? "",
                Username = data.GetProperty("username").GetString() ?? "",
                Password = data.GetProperty("password").GetString() ?? "",
                MaxStores = data.TryGetProperty("maxStores", out var ms) ? ms.GetInt32() : 1,
                MaxUsers = data.TryGetProperty("maxUsers", out var mu) ? mu.GetInt32() : 3,
                ExpiresUtc = data.TryGetProperty("expiresDate", out var exp) ? exp.GetString() ?? "" : "",
                IssuedUtc = DateTime.UtcNow.ToString("O")
            };

            if (!_keyOnly)
                SaveFullActivation(payload);
            else
                SaveLicenseOnly(payload);

            MessageBox.Show(this, "License activated successfully.", "Activation Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            SetStatus(AppBootstrap.RedactSensitiveText(ex.Message), true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ImportOfflineLicense()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import HISAB KITAB License",
            Filter = "HISAB KITAB License (*.hblicense)|*.hblicense|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var payload = ReadAndVerifyOfflineLicense(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(_licenseKey.Text) &&
                !string.Equals(_licenseKey.Text.Trim(), payload.LicenseKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected license file does not match the entered license key.");
            }

            if (!_keyOnly)
                SaveFullActivation(payload);
            else
                SaveLicenseOnly(payload);

            MessageBox.Show(this, "Offline license imported successfully.", "Activation Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            SetStatus(AppBootstrap.RedactSensitiveText(ex.Message), true);
        }
    }

    public static OfflineLicensePayload ReadAndVerifyOfflineLicense(string path)
    {
        var licenseFile = JsonSerializer.Deserialize<OfflineLicenseFile>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("The selected license file is not valid.");
        if (licenseFile.Version != 1)
            throw new InvalidOperationException("This license file version is not supported.");

        var payloadBytes = Convert.FromBase64String(licenseFile.Payload);
        var signatureBytes = Convert.FromBase64String(licenseFile.Signature);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(OfflineLicensePublicKeyBase64), out _);
        if (!rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("The license file signature is invalid.");

        var payload = JsonSerializer.Deserialize<OfflineLicensePayload>(Encoding.UTF8.GetString(payloadBytes))
            ?? throw new InvalidOperationException("The license payload is missing.");
        ValidateOfflinePayload(payload);
        return payload;
    }

    private static void ValidateOfflinePayload(OfflineLicensePayload payload)
    {
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
    }

    private void SaveFullActivation(OfflineLicensePayload payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppBootstrap.ConnectionSettingsPath)!);
        File.WriteAllText(AppBootstrap.ConnectionSettingsPath, JsonSerializer.Serialize(new
        {
            DatabaseType = "SqlServer",
            Server = payload.Server,
            Database = payload.Database,
            Username = payload.Username,
            Password = payload.Password,
            ConnectionString = ""
        }, new JsonSerializerOptions { WriteIndented = true }));

        SaveLicenseOnly(payload);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppBootstrap.ConnectionSettingsPath)!, "pending_store_info.json"), JsonSerializer.Serialize(new
        {
            StoreName = string.IsNullOrWhiteSpace(_storeName.Text) ? payload.BusinessName : _storeName.Text.Trim(),
            StoreAddress = _storeAddress.Text.Trim(),
            StoreZip = _storeZip.Text.Trim()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveLicenseOnly(OfflineLicensePayload payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppBootstrap.LicenseFilePath)!);
        File.WriteAllText(AppBootstrap.LicenseFilePath, JsonSerializer.Serialize(new
        {
            LicenseKey = payload.LicenseKey,
            BusinessName = payload.BusinessName,
            DatabaseName = payload.Database,
            MaxStores = payload.MaxStores,
            MaxUsers = payload.MaxUsers,
            ExpiresDate = payload.ExpiresUtc,
            ActivatedAt = DateTime.UtcNow.ToString("O"),
            ActivationMode = "WinForms"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Please enter the license key.", true);
            return false;
        }
        if (!Regex.IsMatch(key, @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
        {
            SetStatus("Invalid license key format. Expected: HBL-XXXX-XXXX-XXXX", true);
            return false;
        }
        return true;
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
        catch { }

        if ((int)response.StatusCode == 403 && responseText.Contains("web app is stopped", StringComparison.OrdinalIgnoreCase))
            return "The activation server is currently stopped. Import an offline license file or start the activation web app.";

        return $"License validation failed. Server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }

    private void SetBusy(bool busy)
    {
        _activate.Enabled = !busy;
        _offline.Enabled = !busy;
        _cancel.Enabled = !busy;
        _status.Text = busy ? "Working..." : _status.Text;
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.ForeColor = error ? WinTheme.Red : WinTheme.Muted;
        _status.Text = text;
    }
}

internal sealed class OfflineLicenseFile
{
    public int Version { get; set; }
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed class OfflineLicensePayload
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
