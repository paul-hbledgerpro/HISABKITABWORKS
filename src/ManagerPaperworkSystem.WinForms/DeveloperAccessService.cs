using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal enum DeveloperPasswordState
{
    NotConfigured,
    Configured,
    Unreadable
}

internal static class DeveloperAccessService
{
    private const int PasswordIterations = 210_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-DEVELOPER-ACCESS-V1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string ProtectedPath =>
        Path.Combine(AppBootstrap.AppDataPath, "developer-access.protected");

    public static DeveloperPasswordState GetPasswordState()
    {
        if (!File.Exists(ProtectedPath))
            return DeveloperPasswordState.NotConfigured;

        try
        {
            _ = Load();
            return DeveloperPasswordState.Configured;
        }
        catch
        {
            return DeveloperPasswordState.Unreadable;
        }
    }

    public static void SetInitialPassword(string password)
    {
        if (GetPasswordState() != DeveloperPasswordState.NotConfigured)
            throw new InvalidOperationException(
                "A developer password is already configured on this Windows account.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new InvalidOperationException(
                "The developer password must contain at least 8 characters.");

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = DerivePasswordHash(password, salt, PasswordIterations);
        try
        {
            Save(new DeveloperPasswordDocument
            {
                Version = 1,
                Iterations = PasswordIterations,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(hash),
                CreatedUtc = DateTime.UtcNow
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool VerifyPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        var document = Load();
        var salt = Convert.FromBase64String(document.Salt);
        var expectedHash = Convert.FromBase64String(document.PasswordHash);
        if (salt.Length < SaltLength ||
            expectedHash.Length != HashLength ||
            document.Iterations < 100_000)
        {
            throw new InvalidOperationException(
                "The protected developer password file is invalid.");
        }

        var enteredHash = DerivePasswordHash(
            password,
            salt,
            document.Iterations);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                enteredHash,
                expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expectedHash);
            CryptographicOperations.ZeroMemory(enteredHash);
        }
    }

    private static byte[] DerivePasswordHash(
        string password,
        byte[] salt,
        int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashLength);

    private static DeveloperPasswordDocument Load()
    {
        var protectedBytes = File.ReadAllBytes(ProtectedPath);
        var clear = ProtectedData.Unprotect(
            protectedBytes,
            Entropy,
            DataProtectionScope.CurrentUser);
        try
        {
            var document =
                JsonSerializer.Deserialize<DeveloperPasswordDocument>(
                    clear,
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "The protected developer password file is empty.");
            if (document.Version != 1 ||
                string.IsNullOrWhiteSpace(document.Salt) ||
                string.IsNullOrWhiteSpace(document.PasswordHash))
            {
                throw new InvalidOperationException(
                    "The protected developer password file is invalid.");
            }

            return document;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static void Save(DeveloperPasswordDocument document)
    {
        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        var clear = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.CurrentUser);
            var temporaryPath = ProtectedPath + ".new";
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, ProtectedPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private sealed class DeveloperPasswordDocument
    {
        public int Version { get; set; }
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }
}

internal sealed class DeveloperAccessForm : Form
{
    private readonly DeveloperPasswordState _passwordState;
    private readonly TextBox _password = WinTheme.TextBox();
    private readonly TextBox _confirmation = WinTheme.TextBox();
    private readonly Label _status = new();
    private int _failedAttempts;

    public DeveloperAccessForm(DeveloperPasswordState passwordState)
    {
        _passwordState = passwordState;
        WinTheme.Apply(this);
        Text = passwordState == DeveloperPasswordState.NotConfigured
            ? "Create Developer Password"
            : "Developer Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(560, passwordState == DeveloperPasswordState.NotConfigured ? 390 : 320);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _password.UseSystemPasswordChar = true;
        _confirmation.UseSystemPasswordChar = true;
        Controls.Add(BuildContent());
    }

    private Control BuildContent()
    {
        var firstTime =
            _passwordState == DeveloperPasswordState.NotConfigured;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = firstTime ? 6 : 5,
            Padding = new Padding(24),
            BackColor = WinTheme.Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        if (firstTime)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(new Label
        {
            Text = firstTime
                ? "CREATE DEVELOPER PASSWORD"
                : "UNLOCK DEVELOPER SETTINGS",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.BlueDark,
            Font = WinTheme.HeaderFont(17),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = firstTime
                ? "First-time setup for this Windows account. This password will protect POS and invoice automation settings."
                : "Enter the developer password. Access remains unlocked only until HISAB KITAB is closed.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(9.5f),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        root.Controls.Add(Field("DEVELOPER PASSWORD", _password), 0, 2);
        var statusRow = 3;
        if (firstTime)
        {
            root.Controls.Add(
                Field("CONFIRM PASSWORD", _confirmation),
                0,
                3);
            statusRow = 4;
        }

        _status.Dock = DockStyle.Fill;
        _status.ForeColor = WinTheme.Red;
        _status.Font = WinTheme.BodyFont(9);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_status, 0, statusRow);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = WinTheme.Bg
        };
        var unlock = WinTheme.Button(
            firstTime ? "SAVE & UNLOCK" : "UNLOCK",
            true);
        unlock.Width = 180;
        unlock.Click += (_, _) => Submit();
        var cancel = WinTheme.Button("CANCEL");
        cancel.Width = 120;
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(unlock);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, statusRow + 1);

        AcceptButton = unlock;
        CancelButton = cancel;
        Shown += (_, _) => _password.Focus();
        return root;
    }

    private static Control Field(string label, TextBox input)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = WinTheme.Bg
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(9),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 3, 0, 0);
        panel.Controls.Add(input, 0, 1);
        return panel;
    }

    private void Submit()
    {
        try
        {
            if (_passwordState == DeveloperPasswordState.NotConfigured)
            {
                if (_password.Text.Length < 8)
                {
                    ShowFailure(
                        "Use at least 8 characters for the developer password.");
                    return;
                }
                if (!string.Equals(
                        _password.Text,
                        _confirmation.Text,
                        StringComparison.Ordinal))
                {
                    ShowFailure("The confirmation password does not match.");
                    return;
                }

                DeveloperAccessService.SetInitialPassword(_password.Text);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (DeveloperAccessService.VerifyPassword(_password.Text))
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _failedAttempts++;
            if (_failedAttempts >= 5)
            {
                MessageBox.Show(
                    this,
                    "Too many incorrect attempts. Close and reopen HISAB KITAB before trying again.",
                    "Developer Settings Locked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                Close();
                return;
            }

            ShowFailure(
                $"Incorrect developer password. {5 - _failedAttempts} attempt(s) remaining.");
        }
        catch (Exception exception)
        {
            ShowFailure(
                AppBootstrap.RedactSensitiveText(exception.Message));
        }
    }

    private void ShowFailure(string message)
    {
        _status.Text = message;
        _password.Clear();
        _confirmation.Clear();
        _password.Focus();
    }
}
