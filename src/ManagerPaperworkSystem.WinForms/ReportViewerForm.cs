using System.Diagnostics;
using System.Net.Mail;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class ReportViewerForm : Form
{
    private readonly string _title;
    private readonly Func<string, Task> _savePdfAsync;
    private readonly Func<string, string, Task>? _emailPdfAsync;
    private readonly string _defaultEmailRecipient;
    private readonly WebView2 _pdfView;
    private readonly Label _status;
    private readonly Label _loadingMessage;
    private readonly ToolTip _actionToolTip = new();
    private Button? _saveButton;
    private Button? _printButton;
    private Button? _openButton;
    private Button? _emailButton;
    private string? _previewPath;
    private bool _isGenerating;
    private bool _embeddedPreviewAvailable;

    public ReportViewerForm(
        string title,
        Func<string, Task> savePdfAsync,
        Func<string, string, Task>? emailPdfAsync = null,
        string? defaultEmailRecipient = null)
    {
        _title = title;
        _savePdfAsync = savePdfAsync;
        _emailPdfAsync = emailPdfAsync;
        _defaultEmailRecipient = defaultEmailRecipient?.Trim() ?? "";

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Width = 1250;
        Height = 860;
        MinimumSize = new Size(960, 680);
        BackColor = WinTheme.Bg;
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowIcon = true;
        Icon = WinTheme.TryLoadIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 3,
            RowCount = 1
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214));
        _status = new Label
        {
            Text = "Generating report...",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(9.5f),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        };
        heading.Controls.Add(_status, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 7),
            Margin = Padding.Empty
        };
        _printButton = AddActionButton(actions, "\uE749", "Print report", async (_, _) => await PrintReportAsync(), false);
        _saveButton = AddActionButton(actions, "\uE74E", "Save as PDF", async (_, _) => await SavePdfAsync(), true);
        _openButton = AddActionButton(actions, "\uE8E5", "Open externally", async (_, _) => await OpenExternallyAsync(), false);
        if (_emailPdfAsync is not null)
            _emailButton = AddActionButton(actions, "\uE715", "Email report PDF", async (_, _) => await EmailReportAsync(), false);
        heading.Controls.Add(actions, 2, 0);
        root.Controls.Add(heading, 0, 0);

        var previewHost = WinTheme.BorderedPanel(8);
        previewHost.Dock = DockStyle.Fill;
        previewHost.BackColor = Color.White;
        root.Controls.Add(previewHost, 0, 1);

        _pdfView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.White,
            Visible = false
        };
        previewHost.Controls.Add(_pdfView);

        _loadingMessage = new Label
        {
            Text = "Generating your report...",
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(7, 29, 58),
            Font = WinTheme.HeaderFont(16),
            TextAlign = ContentAlignment.MiddleCenter
        };
        previewHost.Controls.Add(_loadingMessage);
        _loadingMessage.BringToFront();

        SetActionButtons(false);

        Shown += async (_, _) => await GenerateAndDisplayReportAsync();
        FormClosed += (_, _) => DisposePreview();
    }

    private Button AddActionButton(FlowLayoutPanel host, string glyph, string accessibleName, EventHandler click, bool primary)
    {
        var button = WinTheme.Button(glyph, primary);
        button.Width = 44;
        button.Height = 40;
        button.Margin = new Padding(6, 0, 0, 0);
        button.Padding = Padding.Empty;
        button.Font = WinTheme.IconFont(13);
        button.AccessibleName = accessibleName;
        button.Click += click;
        _actionToolTip.SetToolTip(button, accessibleName);
        host.Controls.Add(button);
        return button;
    }

    private void SetActionButtons(bool enabled)
    {
        if (_saveButton is not null)
            _saveButton.Enabled = enabled;
        if (_printButton is not null)
            _printButton.Enabled = enabled;
        if (_openButton is not null)
            _openButton.Enabled = enabled;
        if (_emailButton is not null)
            _emailButton.Enabled = enabled;
    }

    private async Task GenerateAndDisplayReportAsync()
    {
        await EnsurePreviewPdfAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;

        try
        {
            _status.Text = "Loading report preview...";
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hisab Kitab",
                "WebView2",
                "ReportViewer");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            await _pdfView.EnsureCoreWebView2Async(environment);
            _pdfView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _pdfView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _pdfView.CoreWebView2.Settings.HiddenPdfToolbarItems =
                CoreWebView2PdfToolbarItems.Save |
                CoreWebView2PdfToolbarItems.SaveAs |
                CoreWebView2PdfToolbarItems.Print;
            _pdfView.CoreWebView2.Navigate(new Uri(_previewPath).AbsoluteUri);
            _pdfView.Visible = true;
            _pdfView.BringToFront();
            _loadingMessage.Visible = false;
            _embeddedPreviewAvailable = true;
            _status.Text = "Report ready";
        }
        catch (Exception ex)
        {
            _embeddedPreviewAvailable = false;
            _pdfView.Visible = false;
            _loadingMessage.Visible = true;
            _loadingMessage.Text =
                "The report is ready.\n\n" +
                "Embedded preview is unavailable on this PC, but you can still Email, Save, Print, or Open the PDF.";
            _status.Text = _emailPdfAsync is null
                ? "Report ready · Use Save, Print, or Open"
                : "Report ready · Email is available";
            Debug.WriteLine(
                $"Embedded report preview unavailable: {AppBootstrap.RedactSensitiveText(ex.Message)}");
        }
    }

    private async Task EnsurePreviewPdfAsync()
    {
        if (!string.IsNullOrWhiteSpace(_previewPath) && File.Exists(_previewPath))
            return;
        if (_isGenerating)
            return;

        _isGenerating = true;
        SetActionButtons(false);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "HisabKitabReports");
            Directory.CreateDirectory(dir);
            _previewPath = Path.Combine(dir, $"{SafeFileName(_title)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            await _savePdfAsync(_previewPath);
            SetActionButtons(true);
        }
        catch (Exception ex)
        {
            _previewPath = null;
            _status.Text = "Report generation failed";
            _loadingMessage.Text = "Report generation failed.";
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Reports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _isGenerating = false;
        }
    }

    private async Task SavePdfAsync()
    {
        await EnsurePreviewPdfAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Save Report PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"{SafeFileName(_title)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.Copy(_previewPath, dialog.FileName, true);
            _status.Text = $"Saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Reports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task PrintReportAsync()
    {
        await EnsurePreviewPdfAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;

        try
        {
            if (_embeddedPreviewAvailable && _pdfView.CoreWebView2 is not null)
            {
                _pdfView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                return;
            }
        }
        catch
        {
            // Fall back to the Windows PDF print handler below.
        }

        try
        {
            Process.Start(new ProcessStartInfo(_previewPath) { UseShellExecute = true, Verb = "print" });
        }
        catch
        {
            await OpenExternallyAsync();
            MessageBox.Show(this, "Use the PDF viewer's Print button to complete printing.", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task OpenExternallyAsync()
    {
        await EnsurePreviewPdfAsync();
        if (!string.IsNullOrWhiteSpace(_previewPath) && File.Exists(_previewPath))
            Process.Start(new ProcessStartInfo(_previewPath) { UseShellExecute = true });
    }

    private async Task EmailReportAsync()
    {
        await EnsurePreviewPdfAsync();
        if (_emailPdfAsync is null || string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;
        if (!MailAddress.TryCreate(_defaultEmailRecipient, out _))
        {
            MessageBox.Show(this,
                "No valid email address is saved for this user or business.\n\n"
                + "An Owner/Admin can add the email address in User Accounts, then reopen this report.",
                "Email Address Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var recipient = _defaultEmailRecipient;

        SetActionButtons(false);
        _status.Text = "Sending report email...";
        try
        {
            await _emailPdfAsync(_previewPath, recipient);
            _status.Text = $"Emailed to {recipient}";
            MessageBox.Show(this,
                $"The report PDF was emailed to:\n{recipient}",
                "Report Email Sent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Report email failed";
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message),
                "Email Report", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetActionButtons(true);
        }
    }

    private void DisposePreview()
    {
        _actionToolTip.Dispose();

        try
        {
            _pdfView.Dispose();
        }
        catch
        {
            // Disposal should not block closing the viewer.
        }

        TryDeleteTempPdf();
    }

    private void TryDeleteTempPdf()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_previewPath) && File.Exists(_previewPath))
                File.Delete(_previewPath);
        }
        catch
        {
            // Temp cleanup failure should never block closing the report viewer.
        }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
    }
}
