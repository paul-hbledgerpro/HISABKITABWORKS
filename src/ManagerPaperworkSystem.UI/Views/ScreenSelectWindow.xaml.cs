using System.Windows;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.UI.Views;

public partial class ScreenSelectWindow : Window
{
    private readonly ISettingsService _settings;

    public ScreenSelectWindow(ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;

        cmb.ItemsSource = new List<string> { "Laptop (15\")", "PC (21\")" };

        Loaded += async (_, _) =>
        {
            var s = await _settings.GetSettingsAsync();
            cmb.SelectedIndex = Math.Max(0, Math.Min(1, s.ScreenMode));
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = await _settings.GetSettingsAsync();
            s.ScreenMode = Math.Max(0, cmb.SelectedIndex);
            await _settings.SaveSettingsAsync(s);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            lbl.Text = ex.Message;
        }
    }
}
