namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed partial class MainForm : Form
{
    private readonly TextBox _server = AdminTheme.TextBox();
    private readonly TextBox _username = AdminTheme.TextBox();
    private readonly TextBox _password = AdminTheme.TextBox(password: true);
    private readonly Button _connect = AdminTheme.Button("CONNECT", true);
    private readonly Button _setupSigning = AdminTheme.Button("SET UP / RESTORE KEY");
    private readonly Button _backupSigning = AdminTheme.Button("BACK UP KEY");
    private readonly Button _signTaxRules = AdminTheme.Button("SIGN TAX RULES");
    private readonly Label _connectionStatus = AdminTheme.Label("●  Not connected", AdminTheme.Muted, 9);
    private readonly Label _signingStatus = AdminTheme.Label("●  Checking signing key", AdminTheme.Muted, 9);

    private readonly TextBox _storeGuid = AdminTheme.TextBox();
    private readonly TextBox _pcId = AdminTheme.TextBox();
    private readonly TextBox _storeName = AdminTheme.TextBox();
    private readonly TextBox _storeZip = AdminTheme.TextBox();
    private readonly ComboBox _databaseName = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.White,
        ForeColor = AdminTheme.Text,
        Font = AdminTheme.Body(10.5f),
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly Button _pasteStoreGuid = AdminTheme.Button("PASTE");
    private readonly Button _pastePcId = AdminTheme.Button("PASTE");
    private readonly Button _pasteStoreName = AdminTheme.Button("PASTE");
    private readonly Button _pasteStoreZip = AdminTheme.Button("PASTE");
    private readonly NumericUpDown _maxDevices = AdminTheme.NumberBox();
    private readonly NumericUpDown _maxBusinesses = AdminTheme.NumberBox();
    private readonly DateTimePicker _expires = new()
    {
        Format = DateTimePickerFormat.Short,
        Font = AdminTheme.Body(10.5f),
        MinDate = DateTime.Today,
        MaxDate = DateTime.Today.AddYears(5),
        Value = DateTime.Today.AddMonths(1)
    };
    private readonly Button _generate = AdminTheme.Button("GENERATE LICENSE KEY", true);
    private readonly Button _clear = AdminTheme.Button("CLEAR FORM");

    private readonly TextBox _licenseOutput = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White,
        ForeColor = AdminTheme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9.5f)
    };
    private readonly Label _resultSummary = AdminTheme.Label("No license key generated", AdminTheme.Muted, 9.5f, true);
    private readonly Button _copyLicense = AdminTheme.Button("COPY LICENSE KEY", true);
    private readonly Button _saveLicense = AdminTheme.Button("SAVE LICENSE FILE");
    private readonly Button _manageBusinesses = AdminTheme.Button("MANAGE BUSINESSES");
    private readonly DataGridView _registeredPcs = new();
    private readonly Label _status = AdminTheme.Label("Connect to the licensing database, then paste the four customer values.", AdminTheme.Muted, 10);

    private LicenseActivationService? _service;
    private DeviceLicenseRequestV2? _protectedRequest;
    private string? _lastLicenseJson;
    private ClientSubscription? _currentSubscription;
    private bool _isConnected;

    public MainForm()
    {
        Text = "HISAB KITAB WORKS - Developer License Generator";
        Icon = AdminTheme.LoadIcon();
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1380, 900);
        MinimumSize = new Size(1180, 760);
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.Sizable;

        _server.Text = "hbstoreledger-server.database.windows.net";
        _storeGuid.CharacterCasing = CharacterCasing.Upper;
        _pcId.CharacterCasing = CharacterCasing.Upper;
        _storeZip.MaxLength = 5;
        _generate.Enabled = false;
        _copyLicense.Enabled = false;
        _saveLicense.Enabled = false;
        _manageBusinesses.Enabled = false;
        ConfigureGrid();
        Controls.Add(BuildLayout());
        WireEvents();
        RefreshSigningStatus();
    }

    private void WireEvents()
    {
        _connect.Click += (_, _) => Connect();
        _setupSigning.Click += (_, _) => RestoreSigningKey();
        _backupSigning.Click += (_, _) => BackupSigningKey();
        _signTaxRules.Click += (_, _) => SignTaxRules();
        _pasteStoreGuid.Click += (_, _) => PasteSimpleField(_storeGuid, "Store GUID");
        _pasteStoreName.Click += (_, _) => PasteSimpleField(_storeName, "Store Name");
        _pasteStoreZip.Click += (_, _) => PasteSimpleField(_storeZip, "Store ZIP");
        _pastePcId.Click += (_, _) => PasteProtectedPcId();
        _generate.Click += (_, _) => GenerateLicense();
        _clear.Click += (_, _) => ClearWorkflow();
        _copyLicense.Click += (_, _) => CopyLicense();
        _saveLicense.Click += (_, _) => SaveLicense();
        _manageBusinesses.Click += (_, _) => ManageBusinesses();
        foreach (var field in new[] { _server, _username, _password })
            field.TextChanged += (_, _) => MarkConnectionStale();
    }

    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(_server.Text) ||
            string.IsNullOrWhiteSpace(_username.Text) ||
            string.IsNullOrWhiteSpace(_password.Text))
        {
            SetStatus("Enter SQL Server, username and password.", true);
            return;
        }

        SetBusy(true, "Connecting and preparing the licensing database...");
        try
        {
            var service = new LicenseActivationService(_server.Text, _username.Text, _password.Text);
            service.TestAndPrepareDatabase();
            _service = service;
            _isConnected = true;
            LoadDatabaseChoices();
            _connectionStatus.Text = "●  Connected to licensing database";
            _connectionStatus.ForeColor = AdminTheme.Green;
            SetStatus("Connected. Paste Store GUID, PC ID, Store Name and Store ZIP, then generate the key.", false);
        }
        catch (Exception ex)
        {
            _service = null;
            _isConnected = false;
            _connectionStatus.Text = "●  Connection failed";
            _connectionStatus.ForeColor = AdminTheme.Red;
            SetStatus($"Could not connect: {ex.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PasteSimpleField(TextBox field, string fieldName)
    {
        try
        {
            var value = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Clipboard does not contain {fieldName}.");
            field.Text = value;
            SetStatus($"{fieldName} pasted.", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void PasteProtectedPcId()
    {
        try
        {
            var value = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Clipboard does not contain a PC ID.");

            if (!value.Contains("HKREQ2-", StringComparison.OrdinalIgnoreCase) && !value.TrimStart().StartsWith('{'))
            {
                _protectedRequest = null;
                _pcId.Text = value;
                SetStatus("Short PC ID pasted. It can renew a PC already stored in the database; a first-time PC requires the customer's PC ID COPY button.", false);
                return;
            }

            var request = ActivationCodeCodec.DecodeRequest(value);
            DeviceRequestValidator.Validate(request);
            MergeProtectedField(_storeGuid, request.StoreGuid, "Store GUID");
            MergeProtectedField(_storeName, request.BusinessName, "Store Name");
            MergeProtectedField(_storeZip, request.StoreZip, "Store ZIP");
            _pcId.Text = request.DeviceId;
            _protectedRequest = request;
            SuggestExistingDatabase(request.BusinessName);
            SetStatus($"Protected PC ID verified for {request.DeviceName}. The customer values are ready to generate.", false);
        }
        catch (Exception ex)
        {
            _protectedRequest = null;
            SetStatus($"PC ID could not be verified: {ex.Message}", true);
        }
    }

    private static void MergeProtectedField(TextBox field, string protectedValue, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(field.Text) &&
            !string.Equals(field.Text.Trim(), protectedValue.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The already-pasted {fieldName} does not match the protected PC information.");
        field.Text = protectedValue;
    }

    private void GenerateLicense()
    {
        if (!_isConnected || _service is null)
        {
            SetStatus("Connect to the licensing database first.", true);
            return;
        }
        if (!SigningKeyStore.IsConfigured)
        {
            SetStatus("Set up or restore the private signing key before generating a license.", true);
            return;
        }

        SetBusy(true, "Matching the Store GUID and protected PC ID...");
        try
        {
            var expiresUtc = DateTime.SpecifyKind(
                _expires.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();
            var preparation = PrepareActivationWithDeveloperDecisions(expiresUtc);
            if (preparation is null)
                return;

            if (!preparation.CreatedStore)
            {
                _maxDevices.Value = Math.Clamp(preparation.Subscription.MaxDevices, 1, 999);
                _maxBusinesses.Value = Math.Clamp(
                    Math.Max((int)_maxBusinesses.Value, preparation.Subscription.MaxStores), 1, 999);
            }
            LoadPcGrid(preparation.Devices);
            _currentSubscription = preparation.Subscription;
            _manageBusinesses.Enabled = true;
            var releaseOtherStores = false;
            var alreadyRegisteredHere = preparation.Devices.Any(x =>
                string.Equals(x.DeviceId, preparation.Request.DeviceId, StringComparison.Ordinal));
            if (!alreadyRegisteredHere && preparation.OtherStoreAssignments.Count > 0)
            {
                var assignmentChoice = CrossStorePcDecisionForm.Choose(
                    this,
                    preparation.Request.DeviceId,
                    preparation.Subscription.DatabaseName,
                    preparation.Subscription.BusinessName,
                    preparation.OtherStoreAssignments);
                if (assignmentChoice is null)
                {
                    SetStatus("PC registration decision cancelled. No license key was generated.", false);
                    return;
                }
                releaseOtherStores = assignmentChoice.Value;
            }
            var seatChoice = ChooseSeatAction(preparation);
            if (seatChoice is null)
            {
                SetStatus("License generation cancelled. Nothing was changed for this PC.", false);
                return;
            }
            seatChoice = seatChoice with { ReleaseOtherStoreAssignments = releaseOtherStores };

            var businessLimit = Math.Max((int)_maxBusinesses.Value, preparation.Subscription.MaxStores);
            var issued = _service.Issue(preparation, seatChoice, businessLimit, expiresUtc);
            _licenseOutput.Text = issued.FormattedLicense;
            _lastLicenseJson = issued.LicenseJson;
            _copyLicense.Enabled = true;
            _saveLicense.Enabled = true;
            _maxDevices.Value = Math.Clamp(issued.MaxDevices, 1, 999);
            _maxBusinesses.Value = Math.Clamp(issued.MaxBusinesses, 1, 999);
            _resultSummary.Text = $"{issued.DisplayLicenseKey}  •  {issued.ResultMessage}  •  Payroll state: {issued.Payload.PayrollState}  •  Services: {issued.Payload.EnabledServices}  •  PC seats: {issued.MaxDevices}";
            _resultSummary.ForeColor = AdminTheme.Green;
            LoadPcGrid(issued.Devices);
            SetStatus("License key generated. Copy and paste it into the customer's License Activation window, or save the license file.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"License generation failed: {ex.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private ActivationPreparation PrepareActivation(DateTime expiresUtc, bool allowBusinessTransfer)
    {
        if (_service is null)
            throw new InvalidOperationException("Connect to the licensing database first.");
        return _service.Prepare(
            _storeGuid.Text,
            _storeName.Text,
            _storeZip.Text,
            _databaseName.Text,
            _pcId.Text,
            _protectedRequest,
            (int)_maxDevices.Value,
            (int)_maxBusinesses.Value,
            expiresUtc,
            allowBusinessTransfer);
    }

    private ActivationPreparation? PrepareActivationWithDeveloperDecisions(DateTime expiresUtc)
    {
        var allowBusinessTransfer = false;
        while (true)
        {
            try
            {
                return PrepareActivation(expiresUtc, allowBusinessTransfer);
            }
            catch (BusinessLimitRequiredException limit)
            {
                var choice = MessageBox.Show(this,
                    $"Adding this store requires {limit.RequiredBusinessCount} licensed business slots.\r\n\r\n" +
                    $"Increase BUSINESS to {limit.RequiredBusinessCount} and continue?",
                    "Increase Business Limit",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (choice != DialogResult.Yes)
                {
                    SetStatus("Business-limit increase cancelled. No licensing ownership was changed.", false);
                    return null;
                }
                _maxBusinesses.Value = Math.Clamp(limit.RequiredBusinessCount, 1, 999);
            }
            catch (BusinessOwnershipConflictException conflict)
            {
                var choice = MessageBox.Show(this,
                    $"{conflict.Message}\r\n\r\n" +
                    "Do you want to MOVE this existing store into the current client's subscription?\r\n\r\n" +
                    "YES: Link the existing SQL database here and deactivate its old duplicate licensing assignment.\r\n" +
                    "NO: Cancel without changing ownership.",
                    "Existing Store Already Licensed",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (choice != DialogResult.Yes)
                {
                    SetStatus("Existing-store transfer cancelled. No licensing ownership was changed.", false);
                    return null;
                }
                allowBusinessTransfer = true;
            }
        }
    }

    private PcSeatChoice? ChooseSeatAction(ActivationPreparation preparation)
    {
        var request = preparation.Request;
        if (preparation.Devices.Any(x => string.Equals(x.DeviceId, request.DeviceId, StringComparison.Ordinal)))
            return new PcSeatChoice(PcSeatAction.RenewSamePc);

        var active = preparation.Devices
            .Where(x => string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                        x.ExpiresDate.ToUniversalTime() > DateTime.UtcNow)
            .Select(x => new RegisteredPcOption(x.DeviceId, x.DeviceName, x.Status, x.ExpiresDate))
            .ToList();
        if (active.Count == 0)
            return new PcSeatChoice(PcSeatAction.FirstPc);

        return PcSeatDecisionForm.Choose(
            this, request.DeviceId, request.DeviceName, active, preparation.Subscription.MaxDevices);
    }

    private void CopyLicense()
    {
        if (string.IsNullOrWhiteSpace(_licenseOutput.Text))
            return;
        try
        {
            Clipboard.SetText(_licenseOutput.Text);
            SetStatus("License key copied. Paste it into the customer activation window.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy the license key: {ex.Message}", true);
        }
    }

    private void SaveLicense()
    {
        if (string.IsNullOrWhiteSpace(_lastLicenseJson))
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save HISAB KITAB PC License",
            Filter = "HISAB KITAB PC License (*.hblicense)|*.hblicense",
            FileName = $"{SafeFileName(_storeName.Text)}_{SafeFileName(_pcId.Text)}.hblicense",
            AddExtension = true,
            DefaultExt = ".hblicense"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, _lastLicenseJson);
            SetStatus($"License file saved: {dialog.FileName}", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save the license file: {ex.Message}", true);
        }
    }

    private void ClearWorkflow()
    {
        _storeGuid.Clear();
        _pcId.Clear();
        _storeName.Clear();
        _storeZip.Clear();
        _databaseName.Text = "";
        _protectedRequest = null;
        _lastLicenseJson = null;
        _currentSubscription = null;
        _licenseOutput.Clear();
        _registeredPcs.Rows.Clear();
        _maxDevices.Value = 1;
        _maxBusinesses.Value = 1;
        _expires.Value = DateTime.Today.AddMonths(1);
        _copyLicense.Enabled = false;
        _saveLicense.Enabled = false;
        _manageBusinesses.Enabled = false;
        _resultSummary.Text = "No license key generated";
        _resultSummary.ForeColor = AdminTheme.Muted;
        SetStatus("Form cleared. Paste the next customer request and select its SQL database.", false);
    }

    private void LoadDatabaseChoices()
    {
        if (_service is null)
            return;
        var selected = _databaseName.Text;
        var databases = _service.ListBusinessDatabases();
        _databaseName.BeginUpdate();
        try
        {
            _databaseName.Items.Clear();
            _databaseName.Items.AddRange(databases.Cast<object>().ToArray());
        }
        finally
        {
            _databaseName.EndUpdate();
        }
        _databaseName.Text = selected;
    }

    private void SuggestExistingDatabase(string businessName)
    {
        if (!string.IsNullOrWhiteSpace(_databaseName.Text) || _databaseName.Items.Count == 0)
            return;

        static string Normalize(string value)
            => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        var businessKey = Normalize(businessName);
        if (businessKey.Length == 0)
            return;
        var matches = _databaseName.Items.Cast<string>()
            .Where(database => Normalize(database).Contains(businessKey, StringComparison.Ordinal))
            .OrderByDescending(database => database.StartsWith("HBStoreLedger_", StringComparison.OrdinalIgnoreCase))
            .ThenBy(database => database.Length)
            .ToArray();
        if (matches.Length > 0)
            _databaseName.Text = matches[0];
    }

    private void ManageBusinesses()
    {
        if (_service is null || _currentSubscription is null)
        {
            SetStatus("Generate or match the primary Store GUID first.", true);
            return;
        }
        using var form = new CustomerBusinessesForm(
            _service.LicensingConnectionString,
            _currentSubscription.CustomerId,
            _currentSubscription.BusinessName,
            (int)_maxBusinesses.Value);
        form.ShowDialog(this);
        SetStatus("Approved businesses updated. Generate the PC license again to include the latest business list.", false);
    }

    private void RestoreSigningKey()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Set Up or Restore HISAB KITAB WORKS Signing Key",
            Filter = "Encrypted signing-key backup (*.hbsigningbackup)|*.hbsigningbackup|Signing key or legacy source (*.txt;*.pem;*.key;*.cs)|*.txt;*.pem;*.key;*.cs|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".hbsigningbackup", StringComparison.OrdinalIgnoreCase))
            {
                var password = SigningKeyPasswordForm.PromptForRestore(this);
                if (password is null)
                    return;
                SigningKeyStore.ImportEncryptedBackup(dialog.FileName, password);
            }
            else
            {
                SigningKeyStore.Import(dialog.FileName);
            }
            RefreshSigningStatus();
            SetStatus("Signing key is ready on this developer PC.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not restore the signing key: {ex.Message}", true);
        }
    }

    private void BackupSigningKey()
    {
        if (!SigningKeyStore.IsConfigured)
        {
            SetStatus("Set up the signing key first.", true);
            return;
        }
        var password = SigningKeyPasswordForm.PromptForBackup(this);
        if (password is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save Encrypted Signing-Key Backup",
            Filter = "HISAB KITAB Signing-Key Backup (*.hbsigningbackup)|*.hbsigningbackup",
            FileName = "HISAB_KITAB_SIGNING_KEY_BACKUP.hbsigningbackup",
            AddExtension = true,
            DefaultExt = ".hbsigningbackup"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            SigningKeyStore.ExportEncryptedBackup(dialog.FileName, password);
            SetStatus("Encrypted signing-key backup created.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not back up the signing key: {ex.Message}", true);
        }
    }

    private void SignTaxRules()
    {
        if (!SigningKeyStore.IsConfigured)
        {
            SetStatus("Set up or restore the private signing key first.", true);
            return;
        }

        using var open = new OpenFileDialog
        {
            Title = "Select verified payroll tax-rule JSON",
            Filter = "Payroll tax rules (*.json)|*.json|All files (*.*)|*.*"
        };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        var version = "update";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(open.FileName));
            version = document.RootElement.GetProperty("Version").GetString() ?? version;
        }
        catch
        {
            // The signer performs the authoritative validation and reports any issue.
        }

        using var save = new SaveFileDialog
        {
            Title = "Save signed payroll tax package",
            Filter = "HISAB KITAB tax package (*.hktax)|*.hktax",
            FileName = $"HISAB_KITAB_TaxRules_{version}.hktax"
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            TaxRulePackageSigner.Sign(open.FileName, save.FileName);
            SetStatus($"Signed payroll tax package created: {save.FileName}", false);
            MessageBox.Show(
                this,
                "The signed package is ready. Attach it to the latest GitHub Release. " +
                "Client PCs will discover it from CHECK TAX UPDATES or the automatic daily check.",
                "Tax Package Created",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus($"Tax package was not created: {ex.Message}", true);
        }
    }

    private void RefreshSigningStatus()
    {
        var configured = SigningKeyStore.IsConfigured;
        _signingStatus.Text = configured
            ? "●  Signing ready for this Windows user"
            : "●  Signing setup required";
        _signingStatus.ForeColor = configured ? AdminTheme.Green : AdminTheme.Red;
        _setupSigning.Text = configured ? "RESTORE / REPLACE KEY" : "SET UP / RESTORE KEY";
        _backupSigning.Enabled = configured;
        _signTaxRules.Enabled = configured;
    }

    private void MarkConnectionStale()
    {
        if (!_isConnected)
            return;
        _isConnected = false;
        _service = null;
        _connectionStatus.Text = "●  Connection settings changed - reconnect";
        _connectionStatus.ForeColor = AdminTheme.Muted;
        _generate.Enabled = false;
    }

    private void ConfigureGrid()
    {
        _registeredPcs.BackgroundColor = Color.White;
        _registeredPcs.ForeColor = AdminTheme.Text;
        _registeredPcs.GridColor = AdminTheme.Panel2;
        _registeredPcs.BorderStyle = BorderStyle.FixedSingle;
        _registeredPcs.ReadOnly = true;
        _registeredPcs.AllowUserToAddRows = false;
        _registeredPcs.AllowUserToDeleteRows = false;
        _registeredPcs.RowHeadersVisible = false;
        _registeredPcs.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _registeredPcs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _registeredPcs.EnableHeadersVisualStyles = false;
        _registeredPcs.ColumnHeadersDefaultCellStyle.BackColor = AdminTheme.Blue;
        _registeredPcs.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _registeredPcs.ColumnHeadersDefaultCellStyle.Font = AdminTheme.Bold(9);
        _registeredPcs.DefaultCellStyle.SelectionBackColor = AdminTheme.Panel2;
        _registeredPcs.DefaultCellStyle.SelectionForeColor = AdminTheme.BlueDark;
        _registeredPcs.Columns.Add("Computer", "Computer");
        _registeredPcs.Columns.Add("PcId", "PC ID");
        _registeredPcs.Columns.Add("Status", "Status");
        _registeredPcs.Columns.Add("Expires", "Expires");
    }

    private void LoadPcGrid(IEnumerable<RegisteredLicensePc> devices)
    {
        _registeredPcs.Rows.Clear();
        foreach (var pc in devices)
            _registeredPcs.Rows.Add(pc.DeviceName, pc.DeviceId, pc.Status, pc.ExpiresDate.ToString("MM/dd/yyyy"));
    }

    private void SetBusy(bool busy, string? message = null)
    {
        UseWaitCursor = busy;
        _connect.Enabled = !busy;
        _generate.Enabled = !busy && _isConnected;
        _setupSigning.Enabled = !busy;
        _backupSigning.Enabled = !busy && SigningKeyStore.IsConfigured;
        _signTaxRules.Enabled = !busy && SigningKeyStore.IsConfigured;
        if (busy && !string.IsNullOrWhiteSpace(message))
            SetStatus(message, false);
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? AdminTheme.Red : AdminTheme.Green;
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Replace(' ', '_');
    }
}
