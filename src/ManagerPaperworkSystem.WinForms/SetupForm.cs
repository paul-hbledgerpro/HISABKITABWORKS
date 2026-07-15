using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class SetupForm : Form
{
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TextBox _storeName = WinTheme.TextBox();
    private readonly TextBox _storeAddress = WinTheme.TextBox();
    private readonly ComboBox _reportType = WinTheme.ComboBox();
    private readonly TextBox _firstName = WinTheme.TextBox();
    private readonly TextBox _lastName = WinTheme.TextBox();
    private readonly TextBox _email = WinTheme.TextBox();
    private readonly TextBox _username = WinTheme.TextBox();
    private readonly TextBox _password = WinTheme.TextBox();
    private readonly ComboBox _securityQuestion = WinTheme.ComboBox();
    private readonly TextBox _securityAnswer = WinTheme.TextBox();
    private readonly Label _status = WinTheme.Label("");

    public SetupForm(ISettingsService settingsService, IAuthService authService, IDbContextFactory<AppDbContext> dbFactory)
    {
        _settingsService = settingsService;
        _authService = authService;
        _dbFactory = dbFactory;

        WinTheme.Apply(this);
        Text = "HISAB KITAB - First Time Setup";
        Size = new Size(780, 720);
        MinimumSize = new Size(720, 650);
        Controls.Add(Build());
        Load += (_, _) => LoadPendingStoreInfo();
    }

    private Control Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(28), RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.Controls.Add(new Label
        {
            Text = "FIRST TIME SETUP",
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(22),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _reportType.Items.AddRange(Enum.GetNames<ReportType>());
        _reportType.SelectedItem = ReportType.ShiftLog.ToString();
        _securityQuestion.Items.AddRange(new object[]
        {
            "What was the name of your first pet?",
            "What city were you born in?",
            "What is your mother's maiden name?",
            "What was the model of your first car?",
            "What is the name of your favorite teacher?"
        });
        _securityQuestion.SelectedIndex = 0;
        _password.UseSystemPasswordChar = true;

        var store = FieldCard("Store Information");
        AddField(store, "Store Name *", _storeName, 0);
        AddField(store, "Store Address", _storeAddress, 1);
        AddField(store, "Default Report", _reportType, 2);
        root.Controls.Add(store, 0, 1);

        var admin = FieldCard("Owner / Admin Account");
        AddField(admin, "First Name *", _firstName, 0);
        AddField(admin, "Last Name *", _lastName, 1);
        AddField(admin, "Email", _email, 2);
        AddField(admin, "Username *", _username, 3);
        AddField(admin, "Password *", _password, 4);
        AddField(admin, "Security Question *", _securityQuestion, 5);
        AddField(admin, "Security Answer *", _securityAnswer, 6);
        root.Controls.Add(admin, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = WinTheme.Button("Complete Setup", true);
        save.Width = 180;
        save.Click += async (_, _) => await SaveAsync();
        var cancel = WinTheme.Button("Cancel");
        cancel.Width = 120;
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        _status.AutoSize = false;
        _status.Width = 380;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        actions.Controls.Add(_status);
        root.Controls.Add(actions, 0, 3);
        return root;
    }

    private static TableLayoutPanel FieldCard(string title)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(14), ColumnCount = 4, RowCount = 8 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        card.Controls.Add(new Label { Text = title, ForeColor = WinTheme.Copper, Font = WinTheme.HeaderFont(14), Dock = DockStyle.Fill }, 0, 0);
        card.SetColumnSpan(card.Controls[0], 4);
        return card;
    }

    private static void AddField(TableLayoutPanel card, string label, Control control, int index)
    {
        var row = index / 2 + 1;
        var col = index % 2 * 2;
        card.Controls.Add(WinTheme.Label(label, true), col, row);
        control.Dock = DockStyle.Fill;
        card.Controls.Add(control, col + 1, row);
    }

    private void LoadPendingStoreInfo()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(AppBootstrap.ConnectionSettingsPath)!, "pending_store_info.json");
            if (!File.Exists(path))
                return;

            var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
            if (json.TryGetProperty("StoreName", out var name))
                _storeName.Text = name.GetString() ?? "";
            if (json.TryGetProperty("StoreAddress", out var address))
                _storeAddress.Text = address.GetString() ?? "";
        }
        catch
        {
            // Prefill is helpful, not required.
        }
    }

    private async Task SaveAsync()
    {
        _status.ForeColor = WinTheme.Red;
        if (string.IsNullOrWhiteSpace(_storeName.Text) ||
            string.IsNullOrWhiteSpace(_firstName.Text) ||
            string.IsNullOrWhiteSpace(_lastName.Text) ||
            string.IsNullOrWhiteSpace(_username.Text) ||
            string.IsNullOrWhiteSpace(_password.Text) ||
            string.IsNullOrWhiteSpace(_securityAnswer.Text))
        {
            _status.Text = "Please fill all required fields.";
            return;
        }

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var store = await db.Stores.FirstOrDefaultAsync();
            if (store is null)
            {
                store = new Store { IsActive = true };
                db.Stores.Add(store);
            }
            store.Name = _storeName.Text.Trim();
            store.Address = _storeAddress.Text.Trim();
            await db.SaveChangesAsync();

            var settings = await _settingsService.GetSettingsAsync();
            settings.StoreName = store.Name;
            settings.StoreAddress = store.Address;
            settings.DefaultStoreId = store.Id;
            settings.LastStoreId = store.Id;
            if (Enum.TryParse<ReportType>(_reportType.Text, out var reportType))
                settings.DefaultReportType = reportType;
            await _settingsService.SaveSettingsAsync(settings);

            if (!await _authService.HasAnyUsersAsync())
            {
                await _authService.CreateUserAsync(
                    _firstName.Text.Trim(),
                    _lastName.Text.Trim(),
                    UserRole.OwnerAdmin,
                    _username.Text.Trim(),
                    _password.Text,
                    _securityQuestion.Text,
                    _securityAnswer.Text,
                    _email.Text.Trim());
            }

            TryDeletePendingStoreInfo();
            _status.ForeColor = WinTheme.Green;
            _status.Text = "Setup complete.";
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            _status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
    }

    private static void TryDeletePendingStoreInfo()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(AppBootstrap.ConnectionSettingsPath)!, "pending_store_info.json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
