using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class StoreManagerForm : Form
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly TextBox _name = WinTheme.TextBox();
    private readonly TextBox _address = WinTheme.TextBox();
    private readonly TextBox _connection = WinTheme.TextBox();

    public StoreManagerForm(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        WinTheme.Apply(this);
        Text = "Store Manager - HISAB KITAB";
        Size = new Size(980, 640);
        Controls.Add(Build());
        Load += (_, _) => RefreshGrid();
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = WinTheme.Bg, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, BackColor = WinTheme.Panel, Padding = new Padding(12) };
        for (var i = 0; i < 6; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.66f));
        fields.Controls.Add(WinTheme.Label("Store Name", true), 0, 0);
        fields.Controls.Add(_name, 1, 0);
        fields.SetColumnSpan(_name, 2);
        fields.Controls.Add(WinTheme.Label("Address", true), 3, 0);
        fields.Controls.Add(_address, 4, 0);
        fields.SetColumnSpan(_address, 2);
        fields.Controls.Add(WinTheme.Label("SQL Connection String", true), 0, 1);
        fields.Controls.Add(_connection, 1, 1);
        fields.SetColumnSpan(_connection, 5);
        root.Controls.Add(fields, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(Button("Close", () => Close()));
        actions.Controls.Add(Button("Delete Store", DeleteSelected));
        actions.Controls.Add(Button("Save Store", SaveStore, true));
        root.Controls.Add(actions, 0, 2);
        _grid.SelectionChanged += (_, _) => PopulateSelected();
        return root;
    }

    private Button Button(string text, Action action, bool filled = false)
    {
        var b = WinTheme.Button(text, filled);
        b.Width = 160;
        b.Click += (_, _) => action();
        return b;
    }

    private void RefreshGrid()
    {
        using var db = _dbFactory.CreateDbContext();
        var conns = AppBootstrap.LoadStoreConnections();
        _grid.DataSource = db.Stores.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Name, x.Address, x.IsActive, x.CreatedUtc, HasCustomConnection = conns.ContainsKey(x.Id.ToString()) })
            .ToList();
    }

    private void PopulateSelected()
    {
        if (_grid.CurrentRow is null) return;
        _name.Text = _grid.CurrentRow.Cells["Name"].Value?.ToString() ?? "";
        _address.Text = _grid.CurrentRow.Cells["Address"].Value?.ToString() ?? "";
        var id = SelectedId();
        var conns = AppBootstrap.LoadStoreConnections();
        _connection.Text = id.HasValue && conns.TryGetValue(id.Value.ToString(), out var conn) ? conn : "";
    }

    private async void SaveStore()
    {
        using var db = _dbFactory.CreateDbContext();
        var id = SelectedId();
        Store store;
        if (id.HasValue)
        {
            store = await db.Stores.FindAsync(id.Value) ?? new Store();
            if (store.Id == 0) db.Stores.Add(store);
        }
        else
        {
            store = new Store();
            db.Stores.Add(store);
        }
        store.Name = _name.Text.Trim();
        store.Address = _address.Text.Trim();
        store.IsActive = true;
        await db.SaveChangesAsync();

        var conns = AppBootstrap.LoadStoreConnections();
        if (string.IsNullOrWhiteSpace(_connection.Text))
            conns.Remove(store.Id.ToString());
        else
            conns[store.Id.ToString()] = _connection.Text.Trim();
        AppBootstrap.SaveStoreConnections(conns);
        RefreshGrid();
    }

    private async void DeleteSelected()
    {
        var id = SelectedId();
        if (id is null) return;
        if (MessageBox.Show(this, "Delete selected store?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        using var db = _dbFactory.CreateDbContext();
        var store = await db.Stores.FindAsync(id.Value);
        if (store is null) return;
        db.Stores.Remove(store);
        await db.SaveChangesAsync();
        var conns = AppBootstrap.LoadStoreConnections();
        conns.Remove(id.Value.ToString());
        AppBootstrap.SaveStoreConnections(conns);
        RefreshGrid();
    }

    private int? SelectedId()
    {
        if (_grid.CurrentRow is null) return null;
        return int.TryParse(_grid.CurrentRow.Cells["Id"].Value?.ToString(), out var id) ? id : null;
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
