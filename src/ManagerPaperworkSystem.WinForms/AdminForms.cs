using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Core.Utils;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class StoreManagerForm : Form
{
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly IServiceProvider _services;

    public StoreManagerForm(IDbContextFactory<AppDbContext> dbFactory, IServiceProvider services)
    {
        _services = services;
        WinTheme.Apply(this);
        Text = "Licensed Businesses - HISAB KITAB";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 690);
        MinimumSize = new Size(900, 610);
        Controls.Add(Build());
        Load += (_, _) => RefreshGrid();
    }

    private Control Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = WinTheme.Bg,
            Padding = new Padding(22, 18, 22, 18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        var message = WinTheme.Label(
            "These businesses are digitally signed into this PC license.\r\n"
            + "Choose the login default, arrange the store lineup, or disconnect an additional store from this PC. "
            + "Disconnecting hides it from login without deleting its database or paid license.");
        message.Dock = DockStyle.Fill;
        message.TextAlign = ContentAlignment.MiddleLeft;
        message.ForeColor = WinTheme.Muted;
        message.AutoSize = false;
        message.Padding = new Padding(6, 0, 6, 0);
        root.Controls.Add(message, 0, 0);
        _grid.Margin = new Padding(4, 0, 4, 10);
        root.Controls.Add(_grid, 0, 1);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            Margin = new Padding(4, 2, 4, 0),
            Padding = new Padding(0, 4, 0, 4)
        };
        actions.Controls.Add(Button("Add Store", AddLicensedStore, true, 150));
        actions.Controls.Add(Button("Import Updated License", ImportUpdatedLicense, false, 220));
        actions.Controls.Add(Button("Set as Login Default", SetSelectedAsDefault, true, 210));
        actions.Controls.Add(Button("Move Up", () => MoveSelected(-1), false, 130));
        actions.Controls.Add(Button("Move Down", () => MoveSelected(1), false, 140));
        actions.Controls.Add(Button("Disconnect / Reconnect", ToggleSelectedConnection, false, 220));
        actions.Controls.Add(Button("Close", () => Close(), false, 120));
        root.Controls.Add(actions, 0, 2);
        return root;
    }

    private async void AddLicensedStore()
    {
        using var activation = new DeviceActivationForm(addingLicensedStore: true);
        if (activation.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            await LicensedBusinessService.SynchronizeAsync(_services);
            RefreshGrid();
            MessageBox.Show(this,
                "The signed business list was updated and all existing licensed stores were preserved.",
                "Licensed Stores Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                AppBootstrap.RedactSensitiveText(ex.Message),
                "Store Synchronization Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private Button Button(string text, Action action, bool filled = false, int width = 170)
    {
        var b = WinTheme.Button(text, filled);
        b.Width = width;
        b.Height = 44;
        b.Margin = new Padding(5);
        b.Click += (_, _) => action();
        return b;
    }

    private void RefreshGrid()
    {
        var businesses = LicensedBusinessService.Load();
        var ordered = StoreDirectoryPreferencesStore.GetOrderedBusinesses(
            businesses,
            includeDisconnected: true);
        _grid.DataSource = ordered
            .Select((business, index) => new
            {
                StoreKey = StoreDirectoryPreferencesStore.Key(business),
                Order = index + 1,
                business.BusinessId,
                Name = business.BusinessName,
                business.StoreGuid,
                business.Address,
                Database = business.DatabaseName,
                Type = business.IsPrimary ? "Primary Login Business" : "Additional Business",
                Default = StoreDirectoryPreferencesStore.IsDefault(business, businesses) ? "Yes" : "",
                Connection = StoreDirectoryPreferencesStore.IsConnected(business, businesses)
                    ? "Connected"
                    : "Disconnected"
            })
            .ToList();
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        if (_grid.Columns.Contains("StoreKey"))
            _grid.Columns["StoreKey"]!.Visible = false;
        if (_grid.Columns.Contains("Order"))
            _grid.Columns["Order"]!.FillWeight = 45;
        if (_grid.Columns.Contains("BusinessId"))
        {
            _grid.Columns["BusinessId"]!.HeaderText = "Business ID";
            _grid.Columns["BusinessId"]!.FillWeight = 65;
        }
        if (_grid.Columns.Contains("Name"))
            _grid.Columns["Name"]!.FillWeight = 115;
        if (_grid.Columns.Contains("StoreGuid"))
        {
            _grid.Columns["StoreGuid"]!.HeaderText = "Store GUID";
            _grid.Columns["StoreGuid"]!.FillWeight = 150;
        }
        if (_grid.Columns.Contains("Address"))
            _grid.Columns["Address"]!.FillWeight = 145;
        if (_grid.Columns.Contains("Database"))
            _grid.Columns["Database"]!.FillWeight = 145;
        if (_grid.Columns.Contains("Type"))
            _grid.Columns["Type"]!.FillWeight = 135;
        if (_grid.Columns.Contains("Default"))
            _grid.Columns["Default"]!.FillWeight = 60;
        if (_grid.Columns.Contains("Connection"))
            _grid.Columns["Connection"]!.FillWeight = 90;
    }

    private LicensedBusinessConnection? SelectedBusiness()
    {
        if (_grid.CurrentRow is null || !_grid.Columns.Contains("StoreKey"))
            return null;
        var key = _grid.CurrentRow.Cells["StoreKey"].Value?.ToString();
        return LicensedBusinessService.Load().FirstOrDefault(business =>
            string.Equals(
                StoreDirectoryPreferencesStore.Key(business),
                key,
                StringComparison.OrdinalIgnoreCase));
    }

    private void SetSelectedAsDefault()
    {
        var business = SelectedBusiness();
        if (business is null)
        {
            MessageBox.Show(this, "Select the store to use by default at login.",
                "Licensed Businesses", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var businesses = LicensedBusinessService.Load();
            StoreDirectoryPreferencesStore.SetDefault(business, businesses);
            RefreshGrid();
            MessageBox.Show(this,
                $"{business.BusinessName} will be preselected the next time a user logs in.",
                "Login Default Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(exception.Message),
                "Default Store", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MoveSelected(int direction)
    {
        var business = SelectedBusiness();
        if (business is null)
            return;
        var businesses = LicensedBusinessService.Load();
        StoreDirectoryPreferencesStore.Move(business, businesses, direction);
        RefreshGrid();
        SelectBusiness(business);
    }

    private async void ToggleSelectedConnection()
    {
        var business = SelectedBusiness();
        if (business is null)
        {
            MessageBox.Show(this, "Select the store to disconnect or reconnect.",
                "Licensed Businesses", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var businesses = LicensedBusinessService.Load();
        var connected = StoreDirectoryPreferencesStore.IsConnected(business, businesses);
        var action = connected ? "disconnect" : "reconnect";
        var detail = connected
            ? "It will disappear from login and the store selector on this PC. Its database and license will not be deleted."
            : "It will return to login and the store selector on this PC.";
        if (MessageBox.Show(
                this,
                $"{char.ToUpperInvariant(action[0])}{action[1..]} {business.BusinessName}?\r\n\r\n{detail}",
                $"{char.ToUpperInvariant(action[0])}{action[1..]} Store",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            StoreDirectoryPreferencesStore.SetConnected(
                business,
                businesses,
                connected: !connected);
            if (connected)
                DisablePortalSync(business);
            await LicensedBusinessService.SynchronizeAsync(_services);
            RefreshGrid();
            MessageBox.Show(this,
                connected
                    ? $"{business.BusinessName} was disconnected from this PC login. You can reconnect it here later."
                    : $"{business.BusinessName} was reconnected to this PC login.",
                "Store Connection Updated",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(exception.Message),
                "Store Connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SelectBusiness(LicensedBusinessConnection business)
    {
        var key = StoreDirectoryPreferencesStore.Key(business);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (string.Equals(
                    row.Cells["StoreKey"].Value?.ToString(),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = true;
                _grid.CurrentCell = row.Cells.Cast<DataGridViewCell>()
                    .First(cell => cell.Visible);
                return;
            }
        }
    }

    private static void DisablePortalSync(LicensedBusinessConnection business)
    {
        var document = PortalSyncSettingsStore.Load();
        var changed = false;
        foreach (var settings in document.Stores.Where(settings =>
                     PortalSyncSettingsStore.IsForBusiness(settings, business)))
        {
            settings.Enabled = false;
            settings.CashSalesSummaryEnabled = false;
            settings.ZReportsEnabled = false;
            settings.LastStatus = "Store disconnected from this PC login.";
            settings.LastCashSummaryStatus = settings.LastStatus;
            settings.LastZReportStatus = settings.LastStatus;
            foreach (var reportKind in Enum.GetValues<PortalSyncReportKind>())
                PortalSyncService.RemoveDailyTask(settings.Id, reportKind);
            changed = true;
        }
        if (changed)
            PortalSyncSettingsStore.Save(document);
    }

    private void ImportUpdatedLicense()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Updated HISAB KITAB PC License",
            Filter = "HISAB KITAB Device License (*.hblicense)|*.hblicense"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var existingDatabases = LicensedBusinessService.Load()
                .Select(x => x.DatabaseName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var result = DeviceLicenseService.InstallLicense(dialog.FileName, existingDatabases);
            if (result.Status != DeviceLicenseStatus.Valid)
                throw new InvalidOperationException(result.Message);
            RefreshGrid();
            MessageBox.Show(this,
                "The updated PC license was installed successfully. Close and reopen HISAB KITAB to apply the approved business list and database connections.",
                "License Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "License Update Rejected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

internal sealed class UserAccountsForm : Form
{
    private readonly IAuthService _auth;
    private readonly DataGridView _grid = WinTheme.Grid();

    public UserAccountsForm(IAuthService auth)
    {
        _auth = auth;
        WinTheme.Apply(this);
        Text = "User Accounts - HISAB KITAB";
        Size = new Size(900, 560);
        Controls.Add(Build());
        Load += (_, _) => RefreshGrid();
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = WinTheme.Bg, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var add = WinTheme.Button("Add User", true);
        add.Width = 130;
        add.Click += (_, _) =>
        {
            if (ProgramServices.TryGet<CreateAccountForm>(out var form))
            {
                using (form)
                    form.ShowDialog(this);
                RefreshGrid();
            }
        };
        var toggle = WinTheme.Button("Activate / Deactivate");
        toggle.Width = 190;
        toggle.Click += async (_, _) => await ToggleSelectedAsync();
        var setPin = WinTheme.Button("Set / Reset PIN", true);
        setPin.Width = 160;
        setPin.Click += (_, _) => SetSelectedPin();
        actions.Controls.Add(add);
        actions.Controls.Add(setPin);
        actions.Controls.Add(toggle);
        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        return root;
    }

    private async void RefreshGrid()
    {
        var users = await _auth.GetUsersAsync();
        _grid.DataSource = users.OrderBy(x => x.Username)
            .Select(x => new
            {
                x.Id,
                Name = x.DisplayName,
                x.Username,
                x.Email,
                x.Role,
                Pin = x.HasPin ? "Configured" : "Not Set",
                x.IsActive,
                x.CreatedUtc,
                x.LastLoginUtc
            })
            .ToList();
        if (_grid.Columns.Contains("Id"))
            _grid.Columns["Id"]!.Visible = false;
    }

    private async Task ToggleSelectedAsync()
    {
        if (_grid.CurrentRow is null || !_grid.Columns.Contains("Id"))
            return;
        if (!int.TryParse(_grid.CurrentRow.Cells["Id"].Value?.ToString(), out var id))
            return;
        var active = bool.TryParse(_grid.CurrentRow.Cells["IsActive"].Value?.ToString(), out var current) && current;
        await _auth.SetUserActiveAsync(id, !active);
        RefreshGrid();
    }

    private void SetSelectedPin()
    {
        if (_grid.CurrentRow is null || !_grid.Columns.Contains("Id"))
        {
            MessageBox.Show(this, "Select a user first.", "User PIN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!int.TryParse(_grid.CurrentRow.Cells["Id"].Value?.ToString(), out var id))
            return;

        var username = _grid.CurrentRow.Cells["Username"].Value?.ToString() ?? "user";
        using var form = new SetUserPinForm(_auth, id, username);
        if (form.ShowDialog(this) == DialogResult.OK)
            RefreshGrid();
    }
}

internal sealed class SetUserPinForm : Form
{
    private readonly IAuthService _auth;
    private readonly int _userId;
    private readonly TextBox _pin = WinTheme.TextBox();
    private readonly TextBox _confirm = WinTheme.TextBox();
    private readonly Label _status = WinTheme.Label("");

    public SetUserPinForm(IAuthService auth, int userId, string username)
    {
        _auth = auth;
        _userId = userId;
        WinTheme.Apply(this);
        Text = $"Set PIN for {username} - HISAB KITAB";
        Size = new Size(540, 330);
        MinimumSize = new Size(500, 300);
        MaximizeBox = false;
        MinimizeBox = false;
        ConfigurePinBox(_pin);
        ConfigurePinBox(_confirm);
        Controls.Add(Build(username));
    }

    private Control Build(string username)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            Padding = new Padding(24),
            ColumnCount = 2,
            RowCount = 5
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var heading = WinTheme.Label($"Assign a 4-digit login PIN to {username}.", true);
        heading.ForeColor = WinTheme.Copper;
        root.Controls.Add(heading, 0, 0);
        root.SetColumnSpan(heading, 2);
        AddField(root, "New PIN", _pin, 1);
        AddField(root, "Confirm PIN", _confirm, 2);
        _status.ForeColor = WinTheme.Red;
        root.Controls.Add(_status, 0, 3);
        root.SetColumnSpan(_status, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = WinTheme.Button("Save PIN", true);
        save.Width = 130;
        save.Click += async (_, _) => await SaveAsync();
        var cancel = WinTheme.Button("Cancel");
        cancel.Width = 100;
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 4);
        root.SetColumnSpan(actions, 2);
        return root;
    }

    private static void AddField(TableLayoutPanel root, string label, Control field, int row)
    {
        root.Controls.Add(WinTheme.Label(label, true), 0, row);
        field.Dock = DockStyle.Fill;
        root.Controls.Add(field, 1, row);
    }

    private static void ConfigurePinBox(TextBox box)
    {
        box.UseSystemPasswordChar = true;
        box.MaxLength = UserCredentialVerifier.PinLength;
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        };
    }

    private async Task SaveAsync()
    {
        if (!UserCredentialVerifier.IsValidPin(_pin.Text))
        {
            _status.Text = "Enter exactly four digits.";
            return;
        }
        if (!string.Equals(_pin.Text, _confirm.Text, StringComparison.Ordinal))
        {
            _status.Text = "The PIN entries do not match.";
            return;
        }

        try
        {
            await _auth.SetUserPinAsync(_userId, _pin.Text);
            MessageBox.Show(this, "The login PIN was saved securely.", "User PIN", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            _status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
    }
}

internal sealed class CreateAccountForm : Form
{
    private readonly IAuthService _auth;
    private readonly TextBox _first = WinTheme.TextBox();
    private readonly TextBox _last = WinTheme.TextBox();
    private readonly TextBox _email = WinTheme.TextBox();
    private readonly TextBox _username = WinTheme.TextBox();
    private readonly TextBox _password = WinTheme.TextBox();
    private readonly TextBox _pin = WinTheme.TextBox();
    private readonly ComboBox _role = WinTheme.ComboBox();
    private readonly ComboBox _question = WinTheme.ComboBox();
    private readonly TextBox _answer = WinTheme.TextBox();
    private readonly Label _status = WinTheme.Label("");

    public CreateAccountForm(IAuthService auth)
    {
        _auth = auth;
        WinTheme.Apply(this);
        Text = "Create User - HISAB KITAB";
        Size = new Size(680, 680);
        MinimumSize = new Size(620, 620);
        Controls.Add(Build());
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(22), RowCount = 11, ColumnCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _password.UseSystemPasswordChar = true;
        _pin.UseSystemPasswordChar = true;
        _pin.MaxLength = UserCredentialVerifier.PinLength;
        _pin.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        };
        _role.Items.AddRange(Enum.GetNames<UserRole>());
        _role.SelectedItem = UserRole.Manager.ToString();
        _question.Items.AddRange(new object[]
        {
            "What was the name of your first pet?",
            "What city were you born in?",
            "What is your mother's maiden name?",
            "What was the model of your first car?",
            "What is the name of your favorite teacher?"
        });
        _question.SelectedIndex = 0;
        Add(root, "First Name *", _first, 0);
        Add(root, "Last Name *", _last, 1);
        Add(root, "Email", _email, 2);
        Add(root, "Role *", _role, 3);
        Add(root, "Username *", _username, 4);
        Add(root, "Password *", _password, 5);
        Add(root, "4-Digit PIN *", _pin, 6);
        Add(root, "Security Question *", _question, 7);
        Add(root, "Security Answer *", _answer, 8);
        root.Controls.Add(_status, 0, 9);
        root.SetColumnSpan(_status, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = WinTheme.Button("Create User", true);
        save.Width = 150;
        save.Click += async (_, _) => await SaveAsync();
        var cancel = WinTheme.Button("Cancel");
        cancel.Width = 110;
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 10);
        root.SetColumnSpan(actions, 2);
        return root;
    }

    private static void Add(TableLayoutPanel root, string label, Control control, int row)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(WinTheme.Label(label, true), 0, row);
        control.Dock = DockStyle.Fill;
        root.Controls.Add(control, 1, row);
    }

    private async Task SaveAsync()
    {
        try
        {
            var role = Enum.TryParse<UserRole>(_role.Text, out var r) ? r : UserRole.Manager;
            if (!UserCredentialVerifier.IsValidPin(_pin.Text))
                throw new InvalidOperationException("PIN must contain exactly 4 digits.");
            await _auth.CreateUserAsync(_first.Text, _last.Text, role, _username.Text, _password.Text, _question.Text, _answer.Text, _email.Text, _pin.Text);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            _status.ForeColor = WinTheme.Red;
            _status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
    }
}

internal sealed class ChangePasswordForm : Form
{
    private readonly IAuthService _auth;
    private readonly ManagerPaperworkSystem.UI.Services.SessionState _session;
    private readonly TextBox _password = WinTheme.TextBox();
    private readonly Label _status = WinTheme.Label("");

    public ChangePasswordForm(IAuthService auth, ManagerPaperworkSystem.UI.Services.SessionState session)
    {
        _auth = auth;
        _session = session;
        WinTheme.Apply(this);
        Text = "Change Password - HISAB KITAB";
        Size = new Size(520, 260);
        _password.UseSystemPasswordChar = true;
        Controls.Add(Build());
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(24), RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.Controls.Add(WinTheme.Label("New Password", true), 0, 0);
        root.Controls.Add(_password, 0, 1);
        root.Controls.Add(_status, 0, 2);
        var save = WinTheme.Button("Save Password", true);
        save.Width = 160;
        save.Click += async (_, _) => await SaveAsync();
        root.Controls.Add(save, 0, 3);
        return root;
    }

    private async Task SaveAsync()
    {
        try
        {
            await _auth.ChangePasswordAsync(_session.UserId, _password.Text);
            MessageBox.Show(this, "Password changed.", "HISAB KITAB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = WinTheme.Red;
            _status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
    }
}

internal sealed class ResetPasswordForm : Form
{
    private readonly IAuthService _auth;
    private readonly TextBox _username = WinTheme.TextBox();
    private readonly TextBox _answer = WinTheme.TextBox();
    private readonly TextBox _newPassword = WinTheme.TextBox();
    private readonly Label _question = WinTheme.Label("");
    private readonly Label _status = WinTheme.Label("");

    public ResetPasswordForm(IAuthService auth)
    {
        _auth = auth;
        WinTheme.Apply(this);
        Text = "Reset Password - HISAB KITAB";
        Size = new Size(620, 420);
        _newPassword.UseSystemPasswordChar = true;
        Controls.Add(Build());
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(24), RowCount = 8 };
        Add(root, "Username", _username, 0);
        var lookup = WinTheme.Button("Load Security Question", true);
        lookup.Click += async (_, _) => await LoadQuestionAsync();
        root.Controls.Add(lookup, 0, 2);
        root.Controls.Add(_question, 0, 3);
        Add(root, "Security Answer", _answer, 4);
        Add(root, "New Password", _newPassword, 5);
        root.Controls.Add(_status, 0, 6);
        var reset = WinTheme.Button("Reset Password", true);
        reset.Click += async (_, _) => await ResetAsync();
        root.Controls.Add(reset, 0, 7);
        return root;
    }

    private static void Add(TableLayoutPanel root, string label, Control control, int row)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(WinTheme.Label(label, true), 0, 0);
        panel.Controls.Add(control, 1, 0);
        root.Controls.Add(panel, 0, row);
    }

    private async Task LoadQuestionAsync()
    {
        try
        {
            _question.Text = await _auth.GetSecurityQuestionAsync(_username.Text) ?? "No security question found.";
        }
        catch (Exception ex)
        {
            _status.ForeColor = WinTheme.Red;
            _status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
    }

    private async Task ResetAsync()
    {
        try
        {
            await _auth.ResetPasswordWithSecurityAnswerAsync(_username.Text, _answer.Text, _newPassword.Text);
            MessageBox.Show(this, "Password reset.", "HISAB KITAB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = WinTheme.Red;
            _status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
    }
}
