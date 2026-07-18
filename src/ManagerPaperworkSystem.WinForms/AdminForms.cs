using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        var message = WinTheme.Label(
            "These businesses are digitally signed into this PC license.\r\n"
            + "To add or remove one, the developer updates the client account and reissues this PC license.");
        message.Dock = DockStyle.Fill;
        message.TextAlign = ContentAlignment.MiddleLeft;
        message.ForeColor = WinTheme.Muted;
        message.AutoSize = false;
        message.Padding = new Padding(6, 0, 6, 0);
        root.Controls.Add(message, 0, 0);
        _grid.Margin = new Padding(4, 0, 4, 10);
        root.Controls.Add(_grid, 0, 1);
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(4, 2, 4, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        actions.Controls.Add(Button("Import Updated License", ImportUpdatedLicense), 1, 0);
        actions.Controls.Add(Button("Add Store", AddLicensedStore, true), 2, 0);
        actions.Controls.Add(Button("Close", () => Close()), 3, 0);
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

    private Button Button(string text, Action action, bool filled = false)
    {
        var b = WinTheme.Button(text, filled);
        b.Dock = DockStyle.Fill;
        b.Margin = new Padding(6, 8, 0, 8);
        b.Click += (_, _) => action();
        return b;
    }

    private void RefreshGrid()
    {
        _grid.DataSource = LicensedBusinessService.Load()
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.BusinessName)
            .Select(x => new
            {
                x.BusinessId,
                Name = x.BusinessName,
                x.StoreGuid,
                x.Address,
                Database = x.DatabaseName,
                Type = x.IsPrimary ? "Primary Login Business" : "Additional Business",
                Licensed = true
            })
            .ToList();
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
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
        if (_grid.Columns.Contains("Licensed"))
            _grid.Columns["Licensed"]!.FillWeight = 70;
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
        actions.Controls.Add(add);
        actions.Controls.Add(toggle);
        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        return root;
    }

    private async void RefreshGrid()
    {
        var users = await _auth.GetUsersAsync();
        _grid.DataSource = users.OrderBy(x => x.Username)
            .Select(x => new { x.Id, Name = x.DisplayName, x.Username, x.Email, x.Role, x.IsActive, x.CreatedUtc, x.LastLoginUtc })
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
}

internal sealed class DatabaseSettingsForm : Form
{
    private readonly IAppPaths _paths;
    private readonly TextBox _settings = WinTheme.TextBox();

    public DatabaseSettingsForm(IAppPaths paths)
    {
        _paths = paths;
        WinTheme.Apply(this);
        Text = "Database Settings - HISAB KITAB";
        Size = new Size(900, 520);
        _settings.Multiline = true;
        _settings.Dock = DockStyle.Fill;
        _settings.ScrollBars = ScrollBars.Both;
        _settings.ReadOnly = true;
        Controls.Add(_settings);
        Load += (_, _) => LoadSettingsText();
    }

    private void LoadSettingsText()
    {
        var lines = new List<string>
        {
            "HISAB KITAB Database Settings",
            "",
            "App Data Folder:",
            _paths.AppDataDirectory,
            "",
            "SQLite Database Path:",
            _paths.DatabasePath,
            "",
            "Connection Settings File:",
            AppBootstrap.ConnectionSettingsPath,
            "",
            "Store Connections File:",
            AppBootstrap.StoreConnectionsPath,
            ""
        };
        if (File.Exists(AppBootstrap.ConnectionSettingsPath))
        {
            lines.Add("connection_settings.json:");
            lines.Add(AppBootstrap.RedactSensitiveText(File.ReadAllText(AppBootstrap.ConnectionSettingsPath)));
        }
        _settings.Text = string.Join(Environment.NewLine, lines);
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
    private readonly ComboBox _role = WinTheme.ComboBox();
    private readonly ComboBox _question = WinTheme.ComboBox();
    private readonly TextBox _answer = WinTheme.TextBox();
    private readonly Label _status = WinTheme.Label("");

    public CreateAccountForm(IAuthService auth)
    {
        _auth = auth;
        WinTheme.Apply(this);
        Text = "Create User - HISAB KITAB";
        Size = new Size(680, 620);
        Controls.Add(Build());
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(22), RowCount = 10, ColumnCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _password.UseSystemPasswordChar = true;
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
        Add(root, "Security Question *", _question, 6);
        Add(root, "Security Answer *", _answer, 7);
        root.Controls.Add(_status, 0, 8);
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
        root.Controls.Add(actions, 0, 9);
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
            await _auth.CreateUserAsync(_first.Text, _last.Text, role, _username.Text, _password.Text, _question.Text, _answer.Text, _email.Text);
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
