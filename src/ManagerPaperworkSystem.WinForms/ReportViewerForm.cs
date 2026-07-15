using System.Diagnostics;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class ReportViewerForm : Form
{
    private readonly string _title;
    private readonly Func<string, Task> _savePdfAsync;
    private readonly Label _status;
    private Button? _saveButton;
    private Button? _previewButton;
    private Button? _printButton;
    private string? _previewPath;
    private bool _isGenerating;

    public ReportViewerForm(string title, Func<string, Task> savePdfAsync)
    {
        _title = title;
        _savePdfAsync = savePdfAsync;

        Text = $"Hisab Kitab Works - {title}";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1100;
        Height = 780;
        MinimumSize = new Size(900, 640);
        BackColor = WinTheme.Bg;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(22)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(21),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);

        var panel = WinTheme.BorderedPanel(28);
        panel.Dock = DockStyle.Fill;
        root.Controls.Add(panel, 0, 1);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel,
            ColumnCount = 1,
            RowCount = 4
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(body);

        body.Controls.Add(new Label
        {
            Text = "Professional PDF Report",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.HeaderFont(19),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        body.Controls.Add(new Label
        {
            Text = "This report is generated as a print-ready PDF using the Hisab Kitab branded report template. Use Print Preview to inspect the final PDF layout before printing.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(10.5f),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel2,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(12)
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        body.Controls.Add(summary, 0, 2);

        AddSummaryMetric(summary, "Report", _title, 0);
        AddSummaryMetric(summary, "Generated", DateTime.Now.ToString("M/d/yyyy h:mm tt"), 1);
        AddSummaryMetric(summary, "Format", "PDF", 2);

        var previewPanel = WinTheme.BorderedPanel(12);
        previewPanel.Dock = DockStyle.Fill;
        previewPanel.Margin = new Padding(10);
        var previewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18)
        };
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        previewPanel.Controls.Add(previewLayout);
        previewLayout.Controls.Add(new Label
        {
            Text = "PDF report preview is ready for review.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.HeaderFont(17),
            TextAlign = ContentAlignment.BottomCenter
        }, 0, 0);
        previewLayout.Controls.Add(new Label
        {
            Text = "Use Print Preview to open the final PDF layout, Save Report as PDF to store it, or Print Report for the printer dialog.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(11),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 1);

        _status = new Label
        {
            Text = "Generating print-ready PDF...",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.BodyFont(10),
            TextAlign = ContentAlignment.TopCenter,
            AutoEllipsis = true
        };
        previewLayout.Controls.Add(_status, 0, 2);
        body.Controls.Add(previewPanel, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 14, 0, 0)
        };
        root.Controls.Add(actions, 0, 2);

        AddButton(actions, "Close", (_, _) => Close(), false);
        _printButton = AddButton(actions, "Print Report", async (_, _) => await PrintReportAsync(), false);
        _previewButton = AddButton(actions, "Print Preview", async (_, _) => await OpenPreviewAsync(), false);
        _saveButton = AddButton(actions, "Save Report as PDF", async (_, _) => await SavePdfAsync(), true);
        SetActionButtons(false);

        Shown += async (_, _) => await EnsurePreviewPdfAsync();
        FormClosed += (_, _) => TryDeleteTempPdf();
    }

    private static void AddSummaryMetric(TableLayoutPanel host, string label, string value, int column)
    {
        var card = WinTheme.BorderedPanel(10);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(6);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        card.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BoldFont(9.5f),
            TextAlign = ContentAlignment.BottomCenter
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(12),
            TextAlign = ContentAlignment.TopCenter,
            AutoEllipsis = true
        }, 0, 1);

        host.Controls.Add(card, column, 0);
    }

    private static Button AddButton(FlowLayoutPanel host, string text, EventHandler click, bool primary)
    {
        var button = WinTheme.Button(text, primary);
        button.Width = text.Length > 14 ? 210 : 160;
        button.Height = 46;
        button.Margin = new Padding(8, 0, 0, 0);
        button.Click += click;
        host.Controls.Add(button);
        return button;
    }

    private void SetActionButtons(bool enabled)
    {
        if (_saveButton is not null)
            _saveButton.Enabled = enabled;
        if (_previewButton is not null)
            _previewButton.Enabled = enabled;
        if (_printButton is not null)
            _printButton.Enabled = enabled;
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
            _status.Text = $"PDF report ready: {_previewPath}";
            SetActionButtons(true);
        }
        catch (Exception ex)
        {
            _previewPath = null;
            _status.Text = "PDF generation failed.";
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
            if (!string.IsNullOrWhiteSpace(_previewPath) && File.Exists(_previewPath))
                File.Copy(_previewPath, dialog.FileName, true);
            else
                await _savePdfAsync(dialog.FileName);
            MessageBox.Show(this, $"Report saved:\n{dialog.FileName}", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Reports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OpenPreviewAsync()
    {
        await EnsurePreviewPdfAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;

        Process.Start(new ProcessStartInfo(_previewPath) { UseShellExecute = true });
    }

    private async Task PrintReportAsync()
    {
        await EnsurePreviewPdfAsync();
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_previewPath) { UseShellExecute = true, Verb = "print" });
        }
        catch
        {
            Process.Start(new ProcessStartInfo(_previewPath) { UseShellExecute = true });
            MessageBox.Show(this, "The PDF was opened for printing. Use the PDF viewer's Print button if Windows does not show the printer dialog automatically.", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
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
