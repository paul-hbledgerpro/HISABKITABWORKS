using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ManagerPaperworkSystem.WinForms;

/// <summary>
/// Displays an already-saved business document without handing it to another
/// desktop application. PDF files use the app's WebView2 runtime; common image
/// formats use a zoomable PictureBox.
/// </summary>
internal sealed class StoredDocumentViewerForm : Form
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    private readonly string _documentPath;
    private readonly string _documentName;
    private readonly Panel _viewerHost;
    private readonly Label _status;
    private WebView2? _pdfView;
    private PictureBox? _imageView;

    public StoredDocumentViewerForm(string documentPath, string documentName)
    {
        _documentPath = Path.GetFullPath(documentPath);
        _documentName = string.IsNullOrWhiteSpace(documentName) ? "Document" : documentName.Trim();

        Text = $"{_documentName} - HISAB KITAB";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1180;
        Height = 820;
        MinimumSize = new Size(900, 640);
        BackColor = WinTheme.Bg;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        Icon = WinTheme.TryLoadIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = WinTheme.Bg,
            Margin = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        heading.Controls.Add(new Label
        {
            Text = _documentName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = WinTheme.HeaderFont(16),
            ForeColor = WinTheme.BlueDark
        }, 0, 0);

        var save = WinTheme.Button("SAVE A COPY", false);
        save.Dock = DockStyle.Fill;
        save.Margin = new Padding(8, 7, 0, 7);
        save.Click += (_, _) => SaveCopy();
        heading.Controls.Add(save, 1, 0);

        var print = WinTheme.Button("PRINT", true);
        print.Dock = DockStyle.Fill;
        print.Margin = new Padding(8, 7, 0, 7);
        print.Click += (_, _) => PrintPdf();
        print.Enabled = string.Equals(Path.GetExtension(_documentPath), ".pdf", StringComparison.OrdinalIgnoreCase);
        heading.Controls.Add(print, 2, 0);
        root.Controls.Add(heading, 0, 0);

        _viewerHost = WinTheme.BorderedPanel(0);
        _viewerHost.Dock = DockStyle.Fill;
        _viewerHost.BackColor = Color.White;
        root.Controls.Add(_viewerHost, 0, 1);

        _status = new Label
        {
            Text = $"Opening {Path.GetFileName(_documentPath)}...",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = WinTheme.BodyFont(9.5f),
            ForeColor = WinTheme.Muted,
            Padding = new Padding(6, 0, 6, 0)
        };
        root.Controls.Add(_status, 0, 2);

        Shown += async (_, _) => await DisplayDocumentAsync();
        FormClosed += (_, _) => DisposeViewer();
    }

    private async Task DisplayDocumentAsync()
    {
        if (!File.Exists(_documentPath))
        {
            ShowUnavailable("The attached document could not be found.");
            return;
        }

        var extension = Path.GetExtension(_documentPath);
        if (ImageExtensions.Contains(extension))
        {
            DisplayImage();
            return;
        }

        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ShowUnavailable("This document type cannot be previewed inside HISAB KITAB.");
            return;
        }

        try
        {
            _pdfView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.White
            };
            _viewerHost.Controls.Add(_pdfView);

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hisab Kitab",
                "WebView2",
                "DocumentViewer");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _pdfView.EnsureCoreWebView2Async(environment);
            _pdfView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _pdfView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _pdfView.CoreWebView2.Settings.HiddenPdfToolbarItems =
                CoreWebView2PdfToolbarItems.Save |
                CoreWebView2PdfToolbarItems.SaveAs |
                CoreWebView2PdfToolbarItems.Print;

            const string hostName = "documents.hisabkitab.local";
            _pdfView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                hostName,
                Path.GetDirectoryName(_documentPath)!,
                CoreWebView2HostResourceAccessKind.Allow);
            var encodedName = Uri.EscapeDataString(Path.GetFileName(_documentPath));
            _pdfView.CoreWebView2.Navigate($"https://{hostName}/{encodedName}");
            _status.Text = $"Viewing inside HISAB KITAB · {Path.GetFileName(_documentPath)}";
        }
        catch (Exception ex)
        {
            ShowUnavailable(
                "The PDF is attached, but the in-app PDF component could not display it on this PC. " +
                "Repair or install Microsoft Edge WebView2 Runtime and try again.",
                ex);
        }
    }

    private void DisplayImage()
    {
        try
        {
            using var source = Image.FromFile(_documentPath);
            _imageView = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(235, 239, 244),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = new Bitmap(source)
            };
            _viewerHost.Controls.Add(_imageView);
            _status.Text = $"Viewing inside HISAB KITAB · {Path.GetFileName(_documentPath)}";
        }
        catch (Exception ex)
        {
            ShowUnavailable("The attached image could not be displayed.", ex);
        }
    }

    private void ShowUnavailable(string message, Exception? exception = null)
    {
        _viewerHost.Controls.Clear();
        _viewerHost.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = WinTheme.HeaderFont(13),
            ForeColor = WinTheme.Red,
            Padding = new Padding(50)
        });
        _status.Text = exception is null
            ? message
            : AppBootstrap.RedactSensitiveText(exception.Message);
    }

    private void SaveCopy()
    {
        using var dialog = new SaveFileDialog
        {
            Title = $"Save {_documentName}",
            FileName = Path.GetFileName(_documentPath),
            Filter = "PDF and image documents (*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff)|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.Copy(_documentPath, dialog.FileName, overwrite: true);
            _status.Text = $"Saved copy: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), _documentName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PrintPdf()
    {
        try
        {
            if (_pdfView?.CoreWebView2 is null)
            {
                MessageBox.Show(this, "Wait for the PDF preview to finish loading, then try again.",
                    _documentName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _pdfView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), _documentName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DisposeViewer()
    {
        try
        {
            _imageView?.Image?.Dispose();
            _imageView?.Dispose();
            _pdfView?.Dispose();
        }
        catch
        {
            // Closing the viewer must not be blocked by cleanup.
        }
    }
}
