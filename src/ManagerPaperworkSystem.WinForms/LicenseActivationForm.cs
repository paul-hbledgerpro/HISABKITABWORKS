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
    private readonly Label _statusDetail = WinTheme.Label("");
    private readonly Label _offlineStatus = WinTheme.Label("");
    private readonly Button _activate = WinTheme.Button("Activate License", true);
    private readonly Button _offline = WinTheme.Button("Import License File");
    private readonly Button _cancel = WinTheme.Button("Cancel");
    private Button? _onlineTab;
    private Button? _offlineTab;
    private Control? _onlinePage;
    private Control? _offlinePage;
    private Panel? _statusCard;
    private Label? _statusIcon;

    public LicenseActivationForm(bool keyOnly)
    {
        _keyOnly = keyOnly;
        WinTheme.Apply(this);
        Text = keyOnly ? "HISAB KITAB - License Key" : "HISAB KITAB - License Activation";
        Size = new Size(1120, keyOnly ? 660 : 760);
        MinimumSize = new Size(900, keyOnly ? 580 : 730);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;

        Controls.Add(Build());
        _activate.Click += async (_, _) => await ActivateOnlineAsync();
        _offline.Click += (_, _) => ImportOfflineLicense();
        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Shown += (_, _) => (_keyOnly ? _licenseKey : _storeName).Focus();
    }

    private Control Build()
    {
        _licenseKey.PlaceholderText = "HBL-XXXX-XXXX-XXXX";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            RowCount = 4,
            ColumnCount = 1,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildTabs(), 0, 1);

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            Padding = new Padding(16, 0, 16, 10),
            AutoScroll = true
        };
        _onlinePage = BuildOnlinePage();
        _offlinePage = BuildOfflinePage();
        contentHost.Controls.Add(_offlinePage);
        contentHost.Controls.Add(_onlinePage);
        root.Controls.Add(contentHost, 0, 2);

        _activate.Text = "ACTIVATE LICENSE";
        _offline.Text = "IMPORT LICENSE FILE";
        _cancel.Text = "CANCEL";
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(16, 12, 16, 10)
        };
        foreach (var button in new[] { _activate, _offline, _cancel })
        {
            button.Width = button == _cancel ? 170 : 230;
            button.Height = 52;
            actions.Controls.Add(button);
        }
        root.Controls.Add(actions, 0, 3);

        SetActivationMode(online: true);
        return root;
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(42, 0, 42, 0) };
        header.Paint += (_, e) => WinTheme.PaintGradient(e, header.ClientRectangle);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 2));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));

        var brand = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 27, 0, 0)
        };
        brand.Controls.Add(new Label { Text = "HISAB", AutoSize = true, ForeColor = WinTheme.Text, Font = WinTheme.HeaderFont(28), Margin = Padding.Empty });
        brand.Controls.Add(new Label { Text = " KITAB", AutoSize = true, ForeColor = WinTheme.Copper, Font = WinTheme.HeaderFont(28), Margin = Padding.Empty });

        var divider = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(90, WinTheme.Muted), Margin = new Padding(0, 25, 0, 25) };
        var title = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(30, 26, 0, 0)
        };
        title.Controls.Add(new Label { Text = "\uE72E", AutoSize = true, ForeColor = WinTheme.Copper, Font = WinTheme.IconFont(28), Margin = new Padding(0, 4, 18, 0) });
        title.Controls.Add(new Label
        {
            Text = _keyOnly ? "LICENSE KEY" : "LICENSE ACTIVATION",
            AutoSize = true,
            ForeColor = WinTheme.Text,
            Font = WinTheme.HeaderFont(22),
            Margin = Padding.Empty
        });

        layout.Controls.Add(brand, 0, 0);
        layout.Controls.Add(divider, 1, 0);
        layout.Controls.Add(title, 2, 0);
        header.Controls.Add(layout);
        return header;
    }

    private Control BuildTabs()
    {
        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(16, 6, 0, 0)
        };
        _onlineTab = CreateTabButton("ONLINE ACTIVATION");
        _offlineTab = CreateTabButton("OFFLINE LICENSE FILE");
        _onlineTab.Click += (_, _) => SetActivationMode(online: true);
        _offlineTab.Click += (_, _) => SetActivationMode(online: false);
        strip.Controls.Add(_onlineTab);
        strip.Controls.Add(_offlineTab);
        return strip;
    }

    private static Button CreateTabButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Width = 260,
            Height = 48,
            FlatStyle = FlatStyle.Flat,
            BackColor = WinTheme.Panel,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BoldFont(11),
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = WinTheme.Panel2;
        button.FlatAppearance.MouseOverBackColor = WinTheme.Panel2;
        button.FlatAppearance.MouseDownBackColor = WinTheme.Panel2;
        return button;
    }

    private Control BuildOnlinePage()
    {
        var page = CreateBorderedSurface(WinTheme.Panel, WinTheme.Panel2);
        page.Dock = DockStyle.Fill;
        page.Padding = new Padding(26, 22, 26, 20);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, ColumnCount = 1, RowCount = 4 };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, _keyOnly ? 0 : 204));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        if (!_keyOnly)
        {
            var businessFields = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, ColumnCount = 2, RowCount = 2 };
            businessFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            businessFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            businessFields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            businessFields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            businessFields.Controls.Add(BuildField("Store / Business Name *", _storeName), 0, 0);
            businessFields.Controls.Add(BuildField("Store Address", _storeAddress), 1, 0);
            businessFields.Controls.Add(BuildField("Store Zip Code", _storeZip, compact: true), 0, 1);
            content.Controls.Add(businessFields, 0, 0);
        }

        var licenseCard = CreateBorderedSurface(WinTheme.Panel2, Color.FromArgb(80, WinTheme.Muted));
        licenseCard.Dock = DockStyle.Fill;
        licenseCard.Margin = new Padding(0, 4, 0, 10);
        licenseCard.Padding = new Padding(18, 10, 18, 12);
        var licenseLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel2, ColumnCount = 2, RowCount = 2 };
        licenseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        licenseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        licenseLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        licenseLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var licenseLabel = WinTheme.FixedLabel("License Key *", false, 11, true);
        licenseLabel.Dock = DockStyle.Fill;
        _licenseKey.Dock = DockStyle.Fill;
        _licenseKey.Margin = new Padding(0, 0, 8, 0);
        var copy = WinTheme.Button("\uE8C8");
        copy.Dock = DockStyle.Fill;
        copy.Font = WinTheme.IconFont(14);
        copy.Margin = Padding.Empty;
        copy.AccessibleName = "Copy license key";
        new ToolTip().SetToolTip(copy, "Copy license key");
        copy.Click += (_, _) => CopyLicenseKey();
        licenseLayout.Controls.Add(licenseLabel, 0, 0);
        licenseLayout.SetColumnSpan(licenseLabel, 2);
        licenseLayout.Controls.Add(_licenseKey, 0, 1);
        licenseLayout.Controls.Add(copy, 1, 1);
        licenseCard.Controls.Add(licenseLayout);
        content.Controls.Add(licenseCard, 0, 1);

        _statusCard = CreateBorderedSurface(WinTheme.Panel, WinTheme.Green);
        _statusCard.Dock = DockStyle.Fill;
        _statusCard.Margin = new Padding(0, 4, 0, 4);
        _statusCard.Padding = new Padding(22, 14, 22, 14);
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, ColumnCount = 2, RowCount = 2 };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        _statusIcon = new Label
        {
            Text = "\uE73E",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Green,
            Font = WinTheme.IconFont(34),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = WinTheme.Green;
        _status.Font = WinTheme.BoldFont(14);
        _status.TextAlign = ContentAlignment.BottomLeft;
        _status.Margin = Padding.Empty;
        _status.Text = "Ready to activate";
        _statusDetail.AutoSize = false;
        _statusDetail.Dock = DockStyle.Fill;
        _statusDetail.ForeColor = WinTheme.Text;
        _statusDetail.Font = WinTheme.BodyFont(10.5f);
        _statusDetail.TextAlign = ContentAlignment.TopLeft;
        _statusDetail.Margin = Padding.Empty;
        _statusDetail.Text = "Your activation details stay securely on this computer.";
        statusLayout.Controls.Add(_statusIcon, 0, 0);
        statusLayout.SetRowSpan(_statusIcon, 2);
        statusLayout.Controls.Add(_status, 1, 0);
        statusLayout.Controls.Add(_statusDetail, 1, 1);
        _statusCard.Controls.Add(statusLayout);
        content.Controls.Add(_statusCard, 0, 2);

        page.Controls.Add(content);
        return page;
    }

    private Control BuildOfflinePage()
    {
        var page = CreateBorderedSurface(WinTheme.Panel, WinTheme.Panel2);
        page.Dock = DockStyle.Fill;
        page.Padding = new Padding(60);

        var card = CreateBorderedSurface(WinTheme.Panel2, WinTheme.CopperDark);
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(40);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel2, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
        layout.Controls.Add(new Label
        {
            Text = "\uE8E5",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.IconFont(38),
            TextAlign = ContentAlignment.BottomCenter
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "IMPORT OFFLINE LICENSE FILE",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.HeaderFont(19),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "Use this option when the computer cannot connect to the activation server.\r\nSelect the signed .hblicense file supplied for this installation.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(11),
            TextAlign = ContentAlignment.TopCenter
        }, 0, 2);
        _offlineStatus.AutoSize = false;
        _offlineStatus.Dock = DockStyle.Fill;
        _offlineStatus.ForeColor = WinTheme.Muted;
        _offlineStatus.Font = WinTheme.BodyFont(10.5f);
        _offlineStatus.TextAlign = ContentAlignment.TopCenter;
        _offlineStatus.Text = "Select IMPORT LICENSE FILE below to continue.";
        layout.Controls.Add(_offlineStatus, 0, 3);
        card.Controls.Add(layout);
        page.Controls.Add(card);
        return page;
    }

    private static Control BuildField(string labelText, TextBox input, bool compact = false)
    {
        var field = new TableLayoutPanel
        {
            Dock = compact ? DockStyle.Left : DockStyle.Fill,
            Width = compact ? 340 : 0,
            BackColor = WinTheme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 28, 8)
        };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var label = WinTheme.FixedLabel(labelText, false, 11);
        label.Dock = DockStyle.Fill;
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }

    private static Panel CreateBorderedSurface(Color background, Color border)
    {
        var panel = new Panel { BackColor = background, Tag = border };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(panel.Tag is Color currentBorder ? currentBorder : border);
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, panel.Width - 1), Math.Max(0, panel.Height - 1));
        };
        return panel;
    }

    private void SetActivationMode(bool online)
    {
        if (_onlinePage is null || _offlinePage is null || _onlineTab is null || _offlineTab is null)
            return;

        _onlinePage.Visible = online;
        _offlinePage.Visible = !online;
        if (online)
            _onlinePage.BringToFront();
        else
            _offlinePage.BringToFront();

        StyleTab(_onlineTab, online);
        StyleTab(_offlineTab, !online);
        _activate.Visible = online;
        _offline.Visible = !online;
    }

    private static void StyleTab(Button button, bool active)
    {
        button.BackColor = active ? WinTheme.Panel2 : WinTheme.Panel;
        button.ForeColor = active ? WinTheme.Text : WinTheme.Muted;
        button.FlatAppearance.BorderColor = active ? WinTheme.Copper : WinTheme.Panel2;
        button.FlatAppearance.BorderSize = active ? 2 : 1;
    }

    private void CopyLicenseKey()
    {
        var value = _licenseKey.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus("Enter a license key before copying it.", true);
            _licenseKey.Focus();
            return;
        }

        try
        {
            Clipboard.SetText(value);
            SetStatus("License key copied to the clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy the license key: {ex.Message}", true);
        }
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
        if (busy)
        {
            _status.Text = "Working...";
            _status.ForeColor = WinTheme.Copper;
            if (_statusIcon is not null)
            {
                _statusIcon.Text = "\uE895";
                _statusIcon.ForeColor = WinTheme.Copper;
            }
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        var color = error ? WinTheme.Red : WinTheme.Green;
        _status.ForeColor = color;
        _status.Text = text;
        _offlineStatus.ForeColor = color;
        _offlineStatus.Text = text;
        _statusDetail.Text = error
            ? "Review the highlighted message and try again."
            : "Your activation details stay securely on this computer.";
        if (_statusIcon is not null)
        {
            _statusIcon.Text = error ? "\uEA39" : "\uE73E";
            _statusIcon.ForeColor = color;
        }
        if (_statusCard is not null)
        {
            _statusCard.Tag = color;
            _statusCard.Invalidate();
        }
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
