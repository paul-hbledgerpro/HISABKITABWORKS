using System;
using System.Threading.Tasks;
using System.Windows;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.UI.Views;

public partial class ResetPasswordWindow : Window
{
    private readonly IAuthService _auth;
    private string _lastUser = "";

    public ResetPasswordWindow(IAuthService auth)
    {
        InitializeComponent();
        _auth = auth;

        txtTargetUser.LostFocus += async (_, __) => await LoadQuestionAsync();
        txtTargetUser.TextChanged += async (_, __) =>
        {
            // Don't spam DB: only refresh when the user changes.
            // LostFocus will also load, this is just for faster feedback.
            var u = (txtTargetUser.Text ?? "").Trim();
            if (u.Length >= 2 && !string.Equals(u, _lastUser, StringComparison.OrdinalIgnoreCase))
                await LoadQuestionAsync();
        };
    }

    private async Task LoadQuestionAsync()
    {
        try
        {
            var u = (txtTargetUser.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(u))
            {
                txtSecurityQuestion.Text = "";
                _lastUser = "";
                return;
            }

            _lastUser = u;
            var q = await _auth.GetSecurityQuestionAsync(u);
            txtSecurityQuestion.Text = string.IsNullOrWhiteSpace(q) ? "(No security question set for this user)" : q;
        }
        catch
        {
            // ignore UI refresh errors
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        try
        {
            var targetUsername = txtTargetUser.Text?.Trim() ?? "";
            var newPass = pwdNew.Password ?? "";
            var confirm = pwdConfirm.Password ?? "";
            var secA = pwdSecurityAnswer.Password ?? "";

            if (string.IsNullOrWhiteSpace(targetUsername))
                throw new Exception("Enter the username to reset.");

            if (string.IsNullOrWhiteSpace(newPass) || newPass.Length < 4)
                throw new Exception("New password must be at least 4 characters.");

            if (newPass != confirm)
                throw new Exception("New password and confirmation do not match.");

            if (string.IsNullOrWhiteSpace(secA) || secA.Trim().Length < 2)
                throw new Exception("Enter the security answer.");

            await _auth.ResetPasswordWithSecurityAnswerAsync(targetUsername, secA, newPass);

            System.Windows.MessageBox.Show(this,
                $"Password reset successfully for: {targetUsername}",
                "Reset Password",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}
