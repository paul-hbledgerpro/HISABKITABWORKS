using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Core.Utils;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class LoginForm : Form
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SessionState _session;
    private readonly TextBox _username = WinTheme.TextBox();
    private readonly TextBox _password = WinTheme.TextBox();
    private readonly Label _error = WinTheme.Label("");
    private readonly ComboBox _storePicker = WinTheme.ComboBox();
    private readonly Button _login = WinTheme.Button("Sign In", true);
    private readonly Button _reset = WinTheme.Button("Reset Password");
    private readonly Button _cancel = WinTheme.Button("Exit");
    private readonly Button _back = WinTheme.Button("Back");
    private readonly List<MatchedStore> _matchedStores = new();
    private readonly List<LoginStoreOption> _availableStores = new();
    private UserAccount? _validatedUser;
    private Label? _storeLabel;
    private Label? _usernameLabel;
    private Label? _passwordLabel;
    private Panel? _storeShell;
    private Panel? _usernameShell;
    private Panel? _passwordShell;

    public LoginForm(IDbContextFactory<AppDbContext> dbFactory, SessionState session)
    {
        _dbFactory = dbFactory;
        _session = session;

        WinTheme.Apply(this);
        Text = "Hisab Kitab Works - User Login";
        ClientSize = new Size(1670, 940);
        MinimumSize = new Size(1280, 720);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        _password.UseSystemPasswordChar = true;
        _error.ForeColor = WinTheme.Red;
        _error.AutoSize = false;
        _error.TextAlign = ContentAlignment.MiddleLeft;
        _error.SetBounds(78, 454, 700, 50);

        var root = new LoginBackgroundPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg
        };
        Controls.Add(root);

        var left = new LoginLeftPanel { Dock = DockStyle.Left, Width = 730 };
        var loginLeftArt = WinTheme.TryLoadLoginLeftArt();
        if (loginLeftArt is not null)
        {
            left.BackgroundImage = loginLeftArt;
            left.BackgroundImageLayout = ImageLayout.Stretch;
        }
        var heroLogo = new PictureBox
        {
            Left = 82,
            Top = 42,
            Width = 500,
            Height = 250,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = WinTheme.TryLoadLogo(),
            BackColor = Color.Transparent
        };
        var heroImage = new PictureBox
        {
            Left = 62,
            Top = 292,
            Width = 590,
            Height = 600,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = WinTheme.TryLoadLoginHero(),
            BackColor = Color.Transparent
        };
        if (loginLeftArt is null)
        {
            left.Controls.Add(heroImage);
            left.Controls.Add(heroLogo);
        }
        root.Controls.Add(left);

        var divider = new Panel { Dock = DockStyle.Left, Width = 2, BackColor = WinTheme.Copper };
        root.Controls.Add(divider);
        divider.BringToFront();

        var right = new LoginRightPanel { Dock = DockStyle.Fill, Padding = new Padding(115, 92, 115, 72) };
        root.Controls.Add(right);
        right.BringToFront();

        var titleIcon = IconLabel("\uE77B", 0, 82, 64, 64, 44);
        var title = new Label
        {
            Text = "User Login",
            Left = 76,
            Top = 86,
            Width = 460,
            Height = 52,
            ForeColor = WinTheme.Text,
            Font = WinTheme.HeaderFont(30),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var subtitle = new Label
        {
            Text = "Please enter your credentials to continue",
            Left = 78,
            Top = 140,
            Width = 560,
            Height = 32,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.BodyFont(15),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };
        right.Controls.Add(titleIcon);
        right.Controls.Add(title);
        right.Controls.Add(subtitle);

        _storeLabel = FormLabel("Store", 78, 224);
        right.Controls.Add(_storeLabel);
        StyleLoginCombo(_storePicker);
        _storeShell = InputShell(78, 254, 700, 60, "\uE8D1", _storePicker);
        right.Controls.Add(_storeShell);

        _usernameLabel = FormLabel("Username", 78, 224);
        right.Controls.Add(_usernameLabel);
        StyleLoginBox(_username);
        SetPlaceholder(_username, "Enter username", false);
        _usernameShell = InputShell(78, 254, 700, 60, "\uE77B", _username);
        right.Controls.Add(_usernameShell);

        _passwordLabel = FormLabel("Password", 78, 354);
        right.Controls.Add(_passwordLabel);
        StyleLoginBox(_password);
        SetPlaceholder(_password, "Enter password", true);
        _passwordShell = InputShell(78, 384, 700, 60, "\uE72E", _password);
        var eye = new Button
        {
            Text = "\uE890",
            Left = 648,
            Top = 11,
            Width = 42,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.IconFont(18),
            Cursor = Cursors.Hand
        };
        eye.FlatAppearance.BorderSize = 0;
        eye.FlatAppearance.MouseOverBackColor = WinTheme.Panel2;
        eye.Click += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_password.UseSystemPasswordChar;
            eye.Text = _password.UseSystemPasswordChar ? "\uE890" : "\uE8F4";
        };
        _passwordShell.Controls.Add(eye);
        right.Controls.Add(_passwordShell);

        right.Controls.Add(_error);

        _login.Text = "\uE8AC  Sign In";
        _reset.Text = "\uE72E  Reset Password";
        _cancel.Text = "\uE8BB  Exit";
        _back.Text = "\uE72B  Back";
        StylePrimaryLoginButton(_login);
        StyleOutlineLoginButton(_reset);
        StyleOutlineLoginButton(_cancel);
        StyleOutlineLoginButton(_back);
        _login.SetBounds(78, 520, 700, 76);
        _reset.SetBounds(78, 620, 330, 62);
        _cancel.SetBounds(430, 620, 348, 62);
        _back.SetBounds(78, 700, 700, 58);
        right.Controls.Add(_login);
        right.Controls.Add(_reset);
        right.Controls.Add(_cancel);
        right.Controls.Add(_back);
        ShowCredentialStep();

        _login.Click += async (_, _) => await LoginAsync();
        _reset.Click += (_, _) =>
        {
            if (ProgramServices.TryGet<ResetPasswordForm>(out var form))
            {
                using (form)
                    form.ShowDialog(this);
            }
        };
        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        _back.Click += (_, _) => ShowCredentialStep();
        _password.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await LoginAsync();
            }
        };
        Shown += async (_, _) =>
        {
            await Task.CompletedTask;
            _username.Focus();
        };
    }

    private static Label FormLabel(string text, int x, int y)
        => new()
        {
            Text = text,
            Left = x,
            Top = y,
            Width = 700,
            Height = 24,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(13),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };

    private static void StyleLoginBox(TextBox box)
    {
        box.BackColor = Color.White;
        box.ForeColor = WinTheme.Text;
        box.BorderStyle = BorderStyle.None;
        box.Font = WinTheme.BodyFont(16);
        box.Margin = Padding.Empty;
    }

    private static void StyleLoginCombo(ComboBox combo)
    {
        combo.BackColor = Color.White;
        combo.ForeColor = WinTheme.Text;
        combo.FlatStyle = FlatStyle.Flat;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Font = WinTheme.BodyFont(15);
    }

    private static void StylePrimaryLoginButton(Button button)
    {
        button.BackColor = WinTheme.Copper;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.Font = WinTheme.BoldFont(22);
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.BorderColor = WinTheme.CopperDark;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(224, 118, 22);
        button.FlatAppearance.MouseDownBackColor = WinTheme.CopperDark;
        button.TextAlign = ContentAlignment.MiddleCenter;
    }

    private static void StyleOutlineLoginButton(Button button)
    {
        button.BackColor = Color.Transparent;
        button.ForeColor = WinTheme.BlueDark;
        button.FlatStyle = FlatStyle.Flat;
        button.Font = WinTheme.BoldFont(14);
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.BorderColor = WinTheme.Blue;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = WinTheme.Panel2;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(229, 238, 248);
    }

    private static Label IconLabel(string glyph, int x, int y, int width, int height, float size)
        => new()
        {
            Text = glyph,
            Left = x,
            Top = y,
            Width = width,
            Height = height,
            ForeColor = WinTheme.Copper,
            BackColor = Color.Transparent,
            Font = WinTheme.IconFont(size),
            TextAlign = ContentAlignment.MiddleCenter
        };

    private static Panel InputShell(int x, int y, int width, int height, string glyph, Control input)
    {
        var shell = new BorderedLoginPanel
        {
            Left = x,
            Top = y,
            Width = width,
            Height = height,
            BackColor = Color.White
        };
        shell.Controls.Add(IconLabel(glyph, 18, 11, 34, 38, 20));
        input.SetBounds(66, input is ComboBox ? 15 : 18, width - 92, 32);
        shell.Controls.Add(input);
        return shell;
    }

    private void ShowCredentialStep()
    {
        _validatedUser = null;
        _matchedStores.Clear();
        _availableStores.Clear();
        _storePicker.DataSource = null;
        _storeLabel!.Visible = false;
        _storeShell!.Visible = false;
        _usernameLabel!.Visible = true;
        _usernameShell!.Visible = true;
        _passwordLabel!.Visible = true;
        _passwordShell!.Visible = true;
        _reset.Visible = true;
        _back.Visible = false;
        _login.Text = "\uE8AC  Sign In";
        _error.ForeColor = WinTheme.Red;
        _error.Text = "";
        _username.Focus();
    }

    private void ShowStoreSelectionStep(List<LoginStoreOption> stores)
    {
        _availableStores.Clear();
        _availableStores.AddRange(stores);
        _storePicker.DataSource = null;
        _storePicker.DisplayMember = nameof(LoginStoreOption.StoreName);
        _storePicker.DataSource = stores;
        if (_storePicker.Items.Count > 0)
            _storePicker.SelectedIndex = 0;

        _usernameLabel!.Visible = false;
        _usernameShell!.Visible = false;
        _passwordLabel!.Visible = false;
        _passwordShell!.Visible = false;
        _storeLabel!.Visible = true;
        _storeShell!.Visible = true;
        _reset.Visible = false;
        _back.Visible = true;
        _login.Text = "\uE8AC  Continue";

        _error.ForeColor = WinTheme.Copper;
        _error.Text = $"Select the store for {CurrentUsername()} and click Continue.";
        _storePicker.Focus();
    }

    private static void SetPlaceholder(TextBox box, string placeholder, bool password)
    {
        box.Text = placeholder;
        box.ForeColor = WinTheme.Muted;
        box.UseSystemPasswordChar = false;

        box.Enter += (_, _) =>
        {
            if (box.Text == placeholder)
            {
                box.Text = "";
                box.ForeColor = WinTheme.Text;
                if (password)
                    box.UseSystemPasswordChar = true;
            }
        };
        box.Leave += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                box.UseSystemPasswordChar = false;
                box.Text = placeholder;
                box.ForeColor = WinTheme.Muted;
            }
        };
    }

    private async Task LoginAsync()
    {
        _error.Text = "";
        var username = CurrentUsername();
        var password = CurrentPassword();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _error.Text = "Username and password are required.";
            return;
        }

        _login.Enabled = false;
        var selectingStore = _storeShell?.Visible == true;
        _login.Text = selectingStore
            ? "\uE8AC  Opening..."
            : "\uE8AC  Checking...";
        Cursor = Cursors.WaitCursor;
        try
        {
            if (selectingStore)
            {
                if (_storePicker.SelectedItem is LoginStoreOption selected)
                {
                    if (_validatedUser is null)
                    {
                        ShowCredentialStep();
                        _error.ForeColor = WinTheme.Red;
                        _error.Text = "Please sign in again before selecting a store.";
                        return;
                    }

                    var matched = await CreateMatchedStoreAsync(selected, _validatedUser);
                    if (matched is null)
                    {
                        _error.ForeColor = WinTheme.Red;
                        _error.Text = "The selected store could not be opened. Please check the store setup.";
                        return;
                    }

                    SelectStore(matched);
                    return;
                }

                _error.ForeColor = WinTheme.Red;
                _error.Text = "Please select a store.";
                return;
            }

            _error.ForeColor = WinTheme.Red;
            _matchedStores.Clear();
            var validatedUser = await ValidateDefaultCredentialsAsync(username, password);
            if (validatedUser is null)
            {
                _error.Text = "Invalid username or password.";
                return;
            }

            _validatedUser = validatedUser;
            var stores = await BuildStoreChoicesAsync();
            if (stores.Count == 0)
            {
                _error.Text = "No stores are configured on this computer.";
                return;
            }

            ShowStoreSelectionStep(stores);
        }
        catch (Exception ex)
        {
            _error.Text = AppBootstrap.RedactSensitiveText(ex.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
            _login.Enabled = true;
            _login.Text = _storeShell?.Visible == true
                ? "\uE8AC  Continue"
                : "\uE8AC  Sign In";
        }
    }

    private string CurrentUsername()
    {
        var username = _username.Text.Trim();
        return username == "Enter username" ? "" : username;
    }

    private string CurrentPassword()
    {
        var password = _password.Text;
        return password == "Enter password" ? "" : password;
    }

    private async Task<List<LoginStoreOption>> BuildStoreChoicesAsync()
    {
        return await Task.Run(async () =>
        {
            var choices = new List<LoginStoreOption>();

            try
            {
                await using var db = _dbFactory.CreateDbContext();
                var localStores = await db.Stores.AsNoTracking().Where(s => s.IsActive).ToListAsync();
                foreach (var store in localStores)
                {
                    choices.Add(new LoginStoreOption
                    {
                        StoreId = store.Id,
                        StoreName = string.IsNullOrWhiteSpace(store.Name) ? $"Store {store.Id}" : store.Name,
                        StoreAddress = store.Address ?? ""
                    });
                }
            }
            catch
            {
                // A broken default database should not stop the user selecting a connected store.
            }

            foreach (var kvp in AppBootstrap.LoadStoreConnections())
            {
                if (!int.TryParse(kvp.Key, out var storeId))
                    continue;

                var connectionString = BuildLoginConnectionString(kvp.Value);
                var name = StoreNameFromConnectionString(connectionString, storeId);
                var existing = choices.FirstOrDefault(x => x.StoreId == storeId);
                if (existing is not null)
                {
                    if (string.IsNullOrWhiteSpace(existing.StoreName) || existing.StoreName.StartsWith("Store ", StringComparison.OrdinalIgnoreCase))
                        existing.StoreName = name;
                    existing.ConnectionString = connectionString;
                    continue;
                }

                choices.Add(new LoginStoreOption
                {
                    StoreId = storeId,
                    StoreName = name,
                    ConnectionString = connectionString
                });
            }

            return choices
                .GroupBy(x => x.StoreId)
                .Select(g => g.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.ConnectionString)).First())
                .OrderBy(x => x.StoreName)
                .ToList();
        });
    }

    private async Task LoadAvailableStoresAsync()
    {
        _availableStores.Clear();
        _availableStores.Add(new LoginStoreOption { StoreName = "Select Store" });

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var localStores = await db.Stores.AsNoTracking().Where(s => s.IsActive).ToListAsync();
            foreach (var store in localStores)
            {
                _availableStores.Add(new LoginStoreOption
                {
                    StoreId = store.Id,
                    StoreName = string.IsNullOrWhiteSpace(store.Name) ? $"Store {store.Id}" : store.Name,
                    StoreAddress = store.Address ?? ""
                });
            }
        }
        catch
        {
            // Local/default database may not be configured yet.
        }

        foreach (var kvp in AppBootstrap.LoadStoreConnections())
        {
            if (!int.TryParse(kvp.Key, out var storeId))
                continue;

            var storeName = $"Store {storeId}";
            var address = "";
            try
            {
                var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(kvp.Value).Options;
                using var db = new AppDbContext(options);
                var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync();
                if (!string.IsNullOrWhiteSpace(store?.Name))
                    storeName = store.Name;
                address = store?.Address ?? "";
            }
            catch
            {
                // Show the saved store entry even if the database is not reachable right now.
            }

            var existing = _availableStores.FirstOrDefault(x => x.StoreId == storeId);
            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(existing.StoreName) || existing.StoreName.Equals($"Store {storeId}", StringComparison.OrdinalIgnoreCase))
                    existing.StoreName = storeName;
                if (string.IsNullOrWhiteSpace(existing.StoreAddress))
                    existing.StoreAddress = address;
                existing.ConnectionString = kvp.Value;
                continue;
            }

            _availableStores.Add(new LoginStoreOption
            {
                StoreId = storeId,
                StoreName = storeName,
                StoreAddress = address,
                ConnectionString = kvp.Value
            });
        }

        var selectedStoreId = _storePicker.SelectedItem is LoginStoreOption current ? current.StoreId : null;
        _storePicker.DataSource = null;
        _storePicker.DisplayMember = nameof(LoginStoreOption.StoreName);
        _storePicker.DataSource = _availableStores
            .GroupBy(x => x.StoreId)
            .Select(g => g.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.ConnectionString)).First())
            .OrderBy(x => x.StoreId is null ? 0 : 1)
            .ThenBy(x => x.StoreName)
            .ToList();
        if (selectedStoreId is int id)
        {
            for (var i = 0; i < _storePicker.Items.Count; i++)
            {
                if (_storePicker.Items[i] is LoginStoreOption item && item.StoreId == id)
                {
                    _storePicker.SelectedIndex = i;
                    return;
                }
            }
        }
        _storePicker.SelectedIndex = 0;
    }

    private async Task<UserAccount?> ValidateDefaultCredentialsAsync(string username, string password)
    {
        return await Task.Run(async () =>
        {
            try
            {
                await using var db = _dbFactory.CreateDbContext();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var normalized = username.Trim().ToLowerInvariant();
                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.IsActive && u.Username.ToLower() == normalized, timeout.Token);

                if (user is null)
                    return null;

                return PasswordHasher.VerifyPassword(password, user.PasswordHashBase64, user.SaltBase64)
                    ? user
                    : null;
            }
            catch
            {
                return null;
            }
        });
    }

    private async Task<MatchedStore?> CreateMatchedStoreAsync(LoginStoreOption selected, UserAccount user)
    {
        if (selected.StoreId is null)
            return null;

        return await Task.Run(async () =>
        {
            var storeName = string.IsNullOrWhiteSpace(selected.StoreName)
                ? $"Store {selected.StoreId.Value}"
                : selected.StoreName;
            var storeAddress = selected.StoreAddress ?? "";
            var connectionString = string.IsNullOrWhiteSpace(selected.ConnectionString)
                ? null
                : BuildLoginConnectionString(selected.ConnectionString);

            try
            {
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    var options = new DbContextOptionsBuilder<AppDbContext>()
                        .UseSqlServer(connectionString, sql => sql.CommandTimeout(6))
                        .Options;
                    await using var db = new AppDbContext(options);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(timeout.Token);
                    if (!string.IsNullOrWhiteSpace(store?.Name))
                        storeName = store.Name;
                    if (!string.IsNullOrWhiteSpace(store?.Address))
                        storeAddress = store.Address;
                }
                else
                {
                    await using var localDb = _dbFactory.CreateDbContext();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var localStore = await localDb.Stores.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == selected.StoreId.Value && s.IsActive, timeout.Token);
                    if (localStore is null)
                        return null;

                    if (!string.IsNullOrWhiteSpace(localStore.Name))
                        storeName = localStore.Name;
                    storeAddress = localStore.Address ?? storeAddress;
                }
            }
            catch
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                    return null;
            }

            return new MatchedStore
            {
                StoreId = selected.StoreId.Value,
                StoreName = storeName,
                StoreAddress = storeAddress,
                ConnectionString = connectionString,
                User = user
            };
        });
    }

    private async Task CheckDefaultDatabaseAsync(string username, string password)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var normalized = username.ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.IsActive && u.Username.ToLower() == normalized);
            if (user is null || !PasswordHasher.VerifyPassword(password, user.PasswordHashBase64, user.SaltBase64))
                return;

            var connectedStores = AppBootstrap.LoadStoreConnections();
            var stores = await db.Stores.AsNoTracking().Where(s => s.IsActive).ToListAsync();
            foreach (var store in stores)
            {
                if (connectedStores.ContainsKey(store.Id.ToString()))
                    continue;

                _matchedStores.Add(new MatchedStore
                {
                    StoreId = store.Id,
                    StoreName = string.IsNullOrWhiteSpace(store.Name) ? "Store" : store.Name,
                    StoreAddress = store.Address ?? "",
                    User = user
                });
            }
        }
        catch
        {
            // Keep login available even if one database cannot be checked.
        }
    }

    private async Task CheckRemoteStoreDatabasesAsync(string username, string password)
    {
        var normalized = username.ToLowerInvariant();
        foreach (var kvp in AppBootstrap.LoadStoreConnections())
        {
            if (!int.TryParse(kvp.Key, out var storeId))
                continue;

            try
            {
                var loginConnectionString = BuildLoginConnectionString(kvp.Value);
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlServer(loginConnectionString, sql => sql.CommandTimeout(5))
                    .Options;
                using var db = new AppDbContext(options);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var user = await db.Users.FirstOrDefaultAsync(u => u.IsActive && u.Username.ToLower() == normalized, timeout.Token);
                if (user is null || !PasswordHasher.VerifyPassword(password, user.PasswordHashBase64, user.SaltBase64))
                    continue;

                var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(timeout.Token);
                _matchedStores.Add(new MatchedStore
                {
                    StoreId = storeId,
                    StoreName = store?.Name ?? $"Store {storeId}",
                    StoreAddress = store?.Address ?? "",
                    ConnectionString = loginConnectionString,
                    User = user
                });
            }
            catch
            {
                // Ignore unreachable remote stores during login.
            }
        }
    }

    private static string BuildLoginConnectionString(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = 5
            };
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static string StoreNameFromConnectionString(string connectionString, int storeId)
    {
        try
        {
            var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            if (string.IsNullOrWhiteSpace(database))
                database = new SqlConnectionStringBuilder(connectionString).DataSource;

            var name = database.Trim().Trim('"', '\'');
            name = Regex.Replace(name, @"^HB\s*Store\s*Ledger[_\s-]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^HBStoreLedger[_\s-]*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^HBLedger[_\s-]*", "", RegexOptions.IgnoreCase);
            name = name.Replace('_', ' ').Replace('-', ' ');
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(name) ? $"Store {storeId}" : name.ToUpperInvariant();
        }
        catch
        {
            return $"Store {storeId}";
        }
    }

    private void SelectStore(MatchedStore store)
    {
        _session.UserId = store.User.Id;
        _session.Username = store.User.Username;
        _session.DisplayName = string.IsNullOrWhiteSpace(store.User.DisplayName) ? store.User.Username : store.User.DisplayName;
        _session.Role = store.User.Role;
        _session.LastStoreId = store.StoreId;
        _session.StoreName = store.StoreName;

        if (ProgramServices.TryGet<AuthService>(out var auth))
        {
            if (!string.IsNullOrWhiteSpace(store.ConnectionString))
            {
                var savedConnections = AppBootstrap.LoadStoreConnections();
                savedConnections[store.StoreId.ToString(CultureInfo.InvariantCulture)] = store.ConnectionString;
                AppBootstrap.SaveStoreConnections(savedConnections);
                auth.SetStoreDbCreator(() => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(store.ConnectionString).Options));
            }
            else
            {
                auth.SetStoreDbCreator(null);
            }
        }

        _ = Task.Run(() =>
        {
            UpdateLastLogin(store);
            PersistSelectedStorePreference(store);
        });

        DialogResult = DialogResult.OK;
    }

    private void UpdateLastLogin(MatchedStore store)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(store.ConnectionString))
            {
                var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(store.ConnectionString).Options;
                using var db = new AppDbContext(options);
                var user = db.Users.FirstOrDefault(u => u.Id == store.User.Id);
                if (user is not null)
                {
                    user.LastLoginUtc = DateTime.UtcNow;
                    db.SaveChanges();
                }
            }
            else
            {
                using var db = _dbFactory.CreateDbContext();
                var user = db.Users.FirstOrDefault(u => u.Id == store.User.Id);
                if (user is not null)
                {
                    user.LastLoginUtc = DateTime.UtcNow;
                    db.SaveChanges();
                }
            }
        }
        catch
        {
            // Last login is useful audit data, but it should not block opening the app.
        }
    }

    private void PersistSelectedStorePreference(MatchedStore store)
    {
        try
        {
            if (!ProgramServices.TryGet<ISettingsService>(out var settingsService))
                return;

            var settings = settingsService.GetSettingsAsync().GetAwaiter().GetResult();
            settings.LastStoreId = store.StoreId;
            settings.DefaultStoreId = store.StoreId;
            settings.StoreName = store.StoreName;
            settings.StoreAddress = store.StoreAddress;
            settingsService.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            // Login should not fail only because the preference could not be written.
        }
    }

    private static bool StoreSelectionMatches(LoginStoreOption selected, MatchedStore matched)
    {
        if (selected.StoreId.HasValue && selected.StoreId.Value == matched.StoreId)
            return true;

        if (!string.IsNullOrWhiteSpace(selected.ConnectionString)
            && !string.IsNullOrWhiteSpace(matched.ConnectionString)
            && string.Equals(selected.ConnectionString, matched.ConnectionString, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(selected.StoreName)
               && !string.Equals(selected.StoreName, "Select Store", StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeStoreName(selected.StoreName), NormalizeStoreName(matched.StoreName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStoreName(string value)
        => Regex.Replace(value ?? "", @"\s+", " ").Trim();

    private sealed class MatchedStore
    {
        public int StoreId { get; set; }
        public string StoreName { get; set; } = "";
        public string StoreAddress { get; set; } = "";
        public string? ConnectionString { get; set; }
        public UserAccount User { get; set; } = null!;
    }

    private sealed class LoginStoreOption
    {
        public int? StoreId { get; set; }
        public string StoreName { get; set; } = "";
        public string StoreAddress { get; set; } = "";
        public string? ConnectionString { get; set; }
        public override string ToString() => StoreName;
    }
}

internal sealed class LoginBackgroundPanel : Panel
{
    public LoginBackgroundPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using var brush = new LinearGradientBrush(bounds, Color.White, WinTheme.Panel2, 0f);
        e.Graphics.FillRectangle(brush, bounds);
    }
}

internal sealed class LoginLeftPanel : Panel
{
    public LoginLeftPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (BackgroundImage is not null)
        {
            base.OnPaintBackground(e);
            return;
        }

        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using var brush = new LinearGradientBrush(bounds, Color.FromArgb(231, 241, 252), Color.White, 90f);
        e.Graphics.FillRectangle(brush, bounds);
    }
}

internal sealed class LoginRightPanel : Panel
{
    public LoginRightPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using var brush = new LinearGradientBrush(bounds, Color.White, Color.FromArgb(248, 251, 255), 20f);
        e.Graphics.FillRectangle(brush, bounds);
    }
}

internal sealed class BorderedLoginPanel : Panel
{
    public BorderedLoginPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(WinTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal static class ProgramServices
{
    private static IServiceProvider? _services;

    public static void Set(IServiceProvider services) => _services = services;

    public static bool TryGet<T>(out T service) where T : class
    {
        service = _services?.GetService(typeof(T)) as T ?? null!;
        return service is not null;
    }
}
