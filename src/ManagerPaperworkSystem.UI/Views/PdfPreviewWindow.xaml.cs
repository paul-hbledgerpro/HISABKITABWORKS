using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ManagerPaperworkSystem.UI.Views;

public partial class PdfPreviewWindow : Window
{
    private readonly List<string> _pdfs;
    private int _index;
    private bool _webReady;
    private readonly bool _autoPrint;

    // WebView2 user data folder — MUST be writable (not Program Files)
    private static readonly string WebView2DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab",
        "WebView2");

    public PdfPreviewWindow(IEnumerable<string> pdfPaths, int startIndex = 0, bool autoPrint = false)
    {
        InitializeComponent();
        _pdfs = new List<string>(pdfPaths ?? Array.Empty<string>());
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _pdfs.Count - 1));
        _autoPrint = autoPrint;

        Loaded += async (_, _) =>
        {
            try
            {
                await EnsureWebAsync();
                NavigateToCurrent();
                if (_autoPrint)
                    await PrintWithDialogAsync();
            }
            catch (Exception ex)
            {
                // If WebView2 can't initialize, show error and allow external open
                System.Diagnostics.Debug.WriteLine($"WebView2 init error: {ex.Message}");
            }
        };
    }

    private async Task EnsureWebAsync()
    {
        if (_webReady) return;

        // Create WebView2 environment with writable UserDataFolder
        Directory.CreateDirectory(WebView2DataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, WebView2DataFolder);
        await web.EnsureCoreWebView2Async(env);

        // Safer defaults for local file viewing
        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;

        _webReady = true;
    }

    private void NavigateToCurrent()
    {
        if (_pdfs.Count == 0)
        {
            txtFile.Text = "(No file)";
            return;
        }

        var path = _pdfs[_index];
        txtFile.Text = Path.GetFileName(path);

        if (!_webReady)
            return;

        var uri = new Uri(path);
        web.Source = uri;
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfs.Count <= 1) return;
        _index = (_index - 1 + _pdfs.Count) % _pdfs.Count;
        NavigateToCurrent();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfs.Count <= 1) return;
        _index = (_index + 1) % _pdfs.Count;
        NavigateToCurrent();
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await PrintWithDialogAsync();
        }
        catch
        {
            // Fallback: open the PDF, user can print from their viewer.
            OpenCurrentExternal();
        }
    }

    private async Task PrintWithDialogAsync()
    {
        await EnsureWebAsync();

        var dlg = new System.Windows.Controls.PrintDialog();
        var ok = dlg.ShowDialog();
        if (ok != true) return;

        var ps = web.CoreWebView2.Environment.CreatePrintSettings();
        try { ps.PrinterName = dlg.PrintQueue?.FullName; } catch { }
        await web.CoreWebView2.PrintAsync(ps);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenCurrentExternal();

    private void OpenCurrentExternal()
    {
        if (_pdfs.Count == 0) return;
        var path = _pdfs[_index];
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
