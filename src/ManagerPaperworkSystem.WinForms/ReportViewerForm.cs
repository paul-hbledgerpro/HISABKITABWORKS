using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class ReportViewerForm : Form
{
    private readonly string _title;
    private readonly Func<string, Task> _savePdfAsync;
    private readonly WebView2 _pdfView;
    private readonly Label _status;
    private readonly Label _loadingMessage;
    private Button? _saveButton;
    private Button? _printButton;
    private Button? _openButton;
    private string? _previewPath;
    private bool _isGenerating;

    public ReportViewerForm(string title, Func<string, Task> savePdfAsync)
    {
        _title = title;
        _savePdfAsync = savePdfAsync;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Width = 1250;
        Height = 860;
        MinimumSize = new Size(960, 680);
        BackColor = WinTheme.Bg;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        Controls.Add(root);

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 2,
            RowCount = 1
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        heading.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(18),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
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

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        root.Controls.Add(actions, 0, 2);

        AddButton(actions, "Close", (_, _) => Close(), false);
        _printButton = AddButton(actions, "Print Report", async (_, _) => await PrintReportAsync(), false);
        _saveButton = AddButton(actions, "Save as PDF", async (_, _) => await SavePdfAsync(), true);
        _openButton = AddButton(actions, "Open Externally", async (_, _) => await OpenExternallyAsync(), false);
        _openButton.Visible = false;
        SetActionButtons(false);

        Shown += async (_, _) => await GenerateAndDisplayReportAsync();
        FormClosed += (_, _) => DisposePreview();
    }

    private static Button AddButton(FlowLayoutPanel host, string text, EventHandler click, bool primary)
    {
        var button = WinTheme.Button(text, primary);
        button.Width = text.Length > 13 ? 175 : 145;
        button.Height = 42;
        button.Margin = new Padding(8, 0, 0, 0);
        button.Click += click;
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
    }

    private async Task GenerateAndDisplayReportAsync()
    {
        await EnsurePreviewPdfAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;

        try
        {
            _status.Text = "Loading report preview...";
            await _pdfView.EnsureCoreWebView2Async();
            _pdfView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _pdfView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _pdfView.CoreWebView2.Navigate(new Uri(_previewPath).AbsoluteUri);
            _pdfView.Visible = true;
            _pdfView.BringToFront();
            _loadingMessage.Visible = false;
            _status.Text = "Report ready";
        }
        catch (Exception ex)
        {
            _pdfView.Visible = false;
            _loadingMessage.Visible = true;
            _loadingMessage.Text = "The embedded PDF viewer is unavailable.\nUse Open Externally to view this report.";
            _status.Text = "External viewer available";
            if (_openButton is not null)
                _openButton.Visible = true;
            MessageBox.Show(this,
                $"The report was generated, but Windows could not start the embedded PDF viewer.\n\n{AppBootstrap.RedactSensitiveText(ex.Message)}",
                "Report Viewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (_pdfView.CoreWebView2 is not null)
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

    private void DisposePreview()
    {
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
