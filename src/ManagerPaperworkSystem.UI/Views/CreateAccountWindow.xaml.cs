using System.Windows;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.UI.Services;

namespace ManagerPaperworkSystem.UI.Views;

public partial class CreateAccountWindow : Window
{
    private readonly IAuthService _auth;
    private readonly SessionState _session;
    private bool _requiresAdminGate;

    public CreateAccountWindow(IAuthService auth, SessionState session)
    {
        InitializeComponent();
        _auth = auth;
        _session = session;

        // Fixed dropdown security questions (Option 1)
        cmbSecurityQuestion.ItemsSource = new[]
        {
            "What was the name of your first pet?",
            "What city were you born in?",
            "What is your mother's maiden name?",
            "What was the model of your first car?",
            "What is the name of your favorite teacher?"
        };
        cmbSecurityQuestion.SelectedIndex = 0;

        cmbRole.ItemsSource = new[] { UserRole.Manager, UserRole.OwnerAdmin };
        cmbRole.SelectedItem = UserRole.Manager;

        Loaded += CreateAccountWindow_Loaded;
    }

    private async void CreateAccountWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // If there are already users, require an Owner/Admin to authorize.
        var anyUsers = await _auth.HasAnyUsersAsync();
        _requiresAdminGate = anyUsers && !_session.IsAdmin;

        if (!anyUsers)
        {
            // First user must be Owner/Admin
            cmbRole.SelectedItem = UserRole.OwnerAdmin;
            cmbRole.IsEnabled = false;
            adminGate.Visibility = Visibility.Collapsed;
            lblInfo.Text = "First-time setup: create the Owner/Admin account.";
        }
        else if (_session.IsAdmin)
        {
            // Admin already logged in
            adminGate.Visibility = Visibility.Collapsed;
            lblInfo.Text = "Create a new account (Owner/Admin or Manager).";
        }
        else
        {
            adminGate.Visibility = Visibility.Visible;
            lblInfo.Text = "Create a new account (requires Owner/Admin authorization).";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";

        var first = txtFirstName.Text?.Trim() ?? "";
        var last = txtLastName.Text?.Trim() ?? "";
        var role = (UserRole)(cmbRole.SelectedItem ?? UserRole.Manager);
        var username = txtUsername.Text?.Trim() ?? "";
        var p1 = pwd1.Password ?? "";
        var p2 = pwd2.Password ?? "";
        var secQ = (cmbSecurityQuestion.SelectedItem as string) ?? "";
        var secA = (pwdSecurityAnswer.Password ?? "").Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            lblError.Text = "Username is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(p1) || p1.Length < 4)
        {
            lblError.Text = "Password must be at least 4 characters.";
            return;
        }
        if (p1 != p2)
        {
            lblError.Text = "Passwords do not match.";
            return;
        }

        if (string.IsNullOrWhiteSpace(secQ))
        {
            lblError.Text = "Please select a security question.";
            return;
        }
        if (string.IsNullOrWhiteSpace(secA) || secA.Length < 2)
        {
            lblError.Text = "Security answer is required.";
            return;
        }

        try
        {
            if (_requiresAdminGate)
            {
                var adminUser = txtAdminUser.Text?.Trim() ?? "";
                var adminPwd = pwdAdmin.Password ?? "";
                var ok = await _auth.VerifyAdminCredentialsAsync(adminUser, adminPwd);
                if (!ok)
                {
                    lblError.Text = "Admin authorization failed.";
                    return;
                }
            }

            await _auth.CreateUserAsync(first, last, role, username, p1, secQ, secA, txtEmail.Text?.Trim() ?? "");
            System.Windows.MessageBox.Show(this, "Account created successfully.", "Create Account", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}
