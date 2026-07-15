using System.Windows;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.UI.Services;

namespace ManagerPaperworkSystem.UI.Views;

public partial class UserAccountsWindow : Window
{
    private readonly IAuthService _auth;
    private readonly SessionState _session;

    public UserAccountsWindow(IAuthService auth, SessionState session)
    {
        InitializeComponent();
        _auth = auth;
        _session = session;
        Loaded += UserAccountsWindow_Loaded;
    }

    private async void UserAccountsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_session.IsAdmin)
        {
            System.Windows.MessageBox.Show(this, "Only Owner/Admin can manage user accounts.", "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        lblError.Text = "";
        var users = await _auth.GetUsersAsync();
        gridUsers.ItemsSource = users;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<CreateAccountWindow>();
        win.Owner = this;
        var ok = win.ShowDialog();
        if (ok == true)
            _ = ReloadAsync();
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        try
        {
            if (gridUsers.SelectedItem is not UserAccount u)
                return;

            // Prevent locking yourself out
            if (u.Id == _session.UserId && u.IsActive)
                throw new Exception("You cannot disable your own account while logged in.");

            await _auth.SetUserActiveAsync(u.Id, !u.IsActive);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}
