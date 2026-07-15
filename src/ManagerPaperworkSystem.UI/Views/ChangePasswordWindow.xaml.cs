using System.Windows;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.UI.Views;

public partial class ChangePasswordWindow : Window
{
    private readonly IAuthService _authService;
    private readonly ManagerPaperworkSystem.UI.Services.SessionState _session;

    public ChangePasswordWindow(IAuthService authService, ManagerPaperworkSystem.UI.Services.SessionState session)
    {
        InitializeComponent();
        _authService = authService;
        _session = session;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        var p1 = pwd1.Password ?? "";
        var p2 = pwd2.Password ?? "";

        if (string.IsNullOrWhiteSpace(p1) || p1.Length < 4)
        {
            lblError.Text = "New password must be at least 4 characters.";
            return;
        }
        if (p1 != p2)
        {
            lblError.Text = "Passwords do not match.";
            return;
        }

        try
        {
            if (_session.UserId <= 0)
                throw new Exception("Not logged in.");
            await _authService.ChangePasswordAsync(_session.UserId, p1);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}
