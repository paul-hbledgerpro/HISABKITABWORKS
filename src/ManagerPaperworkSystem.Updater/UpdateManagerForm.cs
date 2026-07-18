using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ManagerPaperworkSystem.Updater;

/// <summary>
/// Professional Update Manager with GitHub integration.
/// Light blue, orange, green and white theme matching HISAB KITAB.
/// </summary>
public class UpdateManagerForm : Form
{
    // GitHub config — same as main app
    private const string GitHubOwner = "paul-hbledgerpro";
    private const string GitHubRepo = "HISABKITABWORKS";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    private const string PreferredAssetPrefix = "HISAB_KITAB_Update_win-x64";

    // Colors
    private static readonly Color Gold = Color.FromArgb(242, 140, 40);
    private static readonly Color BgDark = Color.FromArgb(244, 248, 252);
    private static readonly Color BgCard = Color.White;
    private static readonly Color BgField = Color.White;
    private static readonly Color BorderColor = Color.FromArgb(210, 220, 232);
    private static readonly Color DimText = Color.FromArgb(100, 116, 139);
    private static readonly Color GreenColor = Color.FromArgb(46, 157, 87);
    private static readonly Color RedColor = Color.FromArgb(214, 69, 69);
    private static readonly Color Navy = Color.FromArgb(22, 58, 95);
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);
    private static readonly Color TextColor = Color.FromArgb(22, 50, 79);
    private static readonly Color OrangeDark = Color.FromArgb(201, 106, 18);

    private readonly string? _appExe;
    private readonly string? _startupDownloadUrl;
    private readonly string? _startupVersion;
    private readonly int? _originatingProcessId;
    private string? _currentVersion;
    private string? _latestVersion;
    private string? _downloadUrl;
    private string? _releaseNotes;

    // Controls
    private Label _lblTitle = null!;
    private Label _lblCurrentVer = null!;
    private Label _lblStatus = null!;
    private Label _lblServerVer = null!;
    private Label _lblReleaseNotes = null!;
    private Button _btnCheck = null!;
    private Button _btnDownload = null!;
    private Button _btnBrowse = null!;
    private Button _btnApplyLocal = null!;
    private Button _btnClose = null!;
    private ProgressBar _progress = null!;
    private Label _lblProgress = null!;
    private Panel _pnlServerInfo = null!;
    private TextBox _txtFilePath = null!;
    private Label _lblStatusDot = null!;

    public UpdateManagerForm(
        string? appExe,
        string? startupDownloadUrl = null,
        string? startupVersion = null,
        string? originatingProcessId = null)
    {
        _appExe = appExe;
        _startupDownloadUrl = startupDownloadUrl;
        _startupVersion = startupVersion;
        _originatingProcessId = int.TryParse(originatingProcessId, out var pid)
            ? pid
            : null;
        DetectCurrentVersion();
        InitializeUI();
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_startupDownloadUrl))
            {
                _downloadUrl = _startupDownloadUrl;
                _latestVersion = _startupVersion ?? "latest";
                _pnlServerInfo.Visible = true;
                _lblServerVer.Text = $"Installing Version: v{_latestVersion}";
                _lblReleaseNotes.Text = "HISAB KITAB will close, then the update will download and install.";
                _lblStatus.ForeColor = GreenColor;
                _lblStatus.Text = "Update approved. Waiting for HISAB KITAB to close...";
                await DownloadAndInstall();
                return;
            }

            await CheckForUpdates();
        };
    }

    private void DetectCurrentVersion()
    {
        _currentVersion = "Unknown";
        if (!string.IsNullOrWhiteSpace(_appExe) && File.Exists(_appExe))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(_appExe);
                if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                    _currentVersion = vi.ProductVersion;
                else if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                    _currentVersion = vi.FileVersion;
            }
            catch { }
        }

        // Also try reading from the UpdateService-style version file
        try
        {
            var dir = AppContext.BaseDirectory;
            var versionFile = Path.Combine(dir, "version.txt");
            if (File.Exists(versionFile))
                _currentVersion = File.ReadAllText(versionFile).Trim();
        }
        catch { }
    }

    private void InitializeUI()
    {
        Text = "HISAB KITAB - Update Manager";
        ClientSize = new Size(760, 690);
        MinimumSize = new Size(700, 650);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = BgDark;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 112,
            BackColor = Navy
        };
        headerPanel.Paint += (s, e) =>
        {
            var bounds = headerPanel.ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
            using var gradient = new LinearGradientBrush(
                bounds,
                Navy,
                Blue,
                LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(gradient, bounds);
            using var accent = new SolidBrush(Gold);
            e.Graphics.FillRectangle(accent, 0, headerPanel.Height - 5, headerPanel.Width, 5);
        };

        _lblTitle = new Label
        {
            Text = "HISAB KITAB WORKS",
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(30, 20),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        var lblSub = new Label
        {
            Text = "SOFTWARE UPDATE MANAGER",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Gold,
            Location = new Point(33, 67),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        _lblStatusDot = new Label
        {
            Text = "●  READY",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(190, 220, 255),
            AutoSize = true,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        headerPanel.Controls.AddRange(new Control[] { _lblTitle, lblSub, _lblStatusDot });
        headerPanel.Layout += (_, _) =>
            _lblStatusDot.Location = new Point(
                Math.Max(30, headerPanel.ClientSize.Width - _lblStatusDot.Width - 32),
                42);
        Controls.Add(headerPanel);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BgDark,
            Padding = new Padding(26, 22, 26, 22),
            ColumnCount = 1,
            RowCount = 6
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(body);

        var statusCard = CreateCard();
        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BgCard,
            Padding = new Padding(18, 12, 14, 12),
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty
        };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _lblCurrentVer = new Label
        {
            Text = $"CURRENT VERSION  •  {_currentVersion}",
            Dock = DockStyle.Fill,
            ForeColor = Navy,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _lblStatus = new Label
        {
            Text = "Checking securely for available updates...",
            Dock = DockStyle.Fill,
            ForeColor = DimText,
            Font = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };
        _btnCheck = CreateButton("CHECK FOR UPDATES", Gold, true);
        _btnCheck.Dock = DockStyle.Fill;
        _btnCheck.Margin = new Padding(12, 4, 0, 4);
        _btnCheck.Click += async (s, e) => await CheckForUpdates();
        statusLayout.Controls.Add(_lblCurrentVer, 0, 0);
        statusLayout.Controls.Add(_lblStatus, 0, 1);
        statusLayout.Controls.Add(_btnCheck, 1, 0);
        statusLayout.SetRowSpan(_btnCheck, 2);
        statusCard.Controls.Add(statusLayout);
        body.Controls.Add(statusCard, 0, 0);

        _pnlServerInfo = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgCard,
            Visible = false,
            Padding = new Padding(18, 14, 18, 14),
            Margin = new Padding(0, 10, 0, 0)
        };
        _pnlServerInfo.Paint += (s, e) =>
        {
            using var pen = new Pen(GreenColor, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, _pnlServerInfo.Width - 1, _pnlServerInfo.Height - 1);
        };
        _lblServerVer = new Label
        {
            Text = "New Version: —",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            ForeColor = GreenColor,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _lblReleaseNotes = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Release information will appear here.",
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Padding = new Padding(0, 5, 0, 4)
        };
        _btnDownload = CreateButton("DOWNLOAD AND INSTALL UPDATE", GreenColor, true);
        _btnDownload.Dock = DockStyle.Bottom;
        _btnDownload.Height = 42;
        _btnDownload.Click += async (s, e) => await DownloadAndInstall();
        _pnlServerInfo.Controls.AddRange(new Control[] { _lblServerVer, _lblReleaseNotes, _btnDownload });
        body.Controls.Add(_pnlServerInfo, 0, 1);

        var progressHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgDark,
            Margin = new Padding(0, 8, 0, 0)
        };
        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 20,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };
        _lblProgress = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = DimText,
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _lblProgress.Visible = false;
        progressHost.Controls.Add(_lblProgress);
        progressHost.Controls.Add(_progress);
        body.Controls.Add(progressHost, 0, 2);

        var localCard = CreateCard();
        localCard.Margin = new Padding(0, 8, 0, 0);
        var localLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BgCard,
            Padding = new Padding(18, 10, 18, 12),
            ColumnCount = 2,
            RowCount = 3
        };
        localLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        localLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        localLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        localLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        localLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var lblLocal = new Label
        {
            Text = "ADVANCED  •  INSTALL FROM A LOCAL UPDATE FILE",
            Dock = DockStyle.Fill,
            ForeColor = Navy,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        localLayout.Controls.Add(lblLocal, 0, 0);
        localLayout.SetColumnSpan(lblLocal, 2);
        _txtFilePath = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgField,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true,
            Text = "No update file selected",
            Font = new Font("Segoe UI", 10),
            Margin = new Padding(0, 2, 8, 2)
        };
        _btnBrowse = CreateButton("BROWSE", Color.White, false);
        _btnBrowse.Dock = DockStyle.Fill;
        _btnBrowse.Margin = new Padding(8, 0, 0, 0);
        _btnBrowse.Click += (s, e) => BrowseLocal();
        _btnApplyLocal = CreateButton("APPLY LOCAL UPDATE", Gold, true);
        _btnApplyLocal.Dock = DockStyle.Fill;
        _btnApplyLocal.Margin = new Padding(0, 8, 0, 0);
        _btnApplyLocal.Enabled = false;
        _btnApplyLocal.Click += (s, e) => ApplyLocal();
        localLayout.Controls.Add(_txtFilePath, 0, 1);
        localLayout.Controls.Add(_btnBrowse, 1, 1);
        localLayout.Controls.Add(_btnApplyLocal, 0, 2);
        localLayout.SetColumnSpan(_btnApplyLocal, 2);
        localCard.Controls.Add(localLayout);
        body.Controls.Add(localCard, 0, 3);

        _btnClose = CreateButton("CLOSE", Color.White, false);
        _btnClose.Dock = DockStyle.Right;
        _btnClose.Width = 150;
        _btnClose.Margin = new Padding(0, 8, 0, 0);
        _btnClose.Click += (s, e) => Close();
        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgDark,
            Margin = Padding.Empty
        };
        footer.Controls.Add(_btnClose);
        body.Controls.Add(footer, 0, 5);
    }

    // ═══════════════════════════════════════════════════════════════
    // CHECK GITHUB FOR UPDATES
    // ═══════════════════════════════════════════════════════════════
    private async Task CheckForUpdates()
    {
        _btnCheck.Enabled = false;
        _lblStatus.ForeColor = DimText;
        _lblStatus.Text = "Checking GitHub for updates...";
        _pnlServerInfo.Visible = false;
        _lblStatusDot.ForeColor = DimText;

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "HisabKitab-Updater");

            var json = await client.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _latestVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            _releaseNotes = root.GetProperty("body").GetString() ?? "";

            // Prefer the update package generated by installer/publish.ps1.
            _downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                var releaseAssets = assets.EnumerateArray()
                    .Select(asset => new
                    {
                        Name = asset.GetProperty("name").GetString() ?? "",
                        Url = asset.GetProperty("browser_download_url").GetString()
                    })
                    .Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                _downloadUrl = releaseAssets
                    .FirstOrDefault(asset => asset.Name.StartsWith(PreferredAssetPrefix, StringComparison.OrdinalIgnoreCase))?.Url
                    ?? releaseAssets.FirstOrDefault()?.Url;
            }

            _lblStatusDot.ForeColor = GreenColor;

            // Compare versions
            bool isNewer = IsNewerVersion(_latestVersion, _currentVersion ?? "0.0.0");

            if (isNewer && !string.IsNullOrWhiteSpace(_downloadUrl))
            {
                _pnlServerInfo.Visible = true;
                _lblServerVer.Text = $"New Version Available: v{_latestVersion}";
                _lblReleaseNotes.Text = _releaseNotes?.Length > 120
                    ? _releaseNotes[..120] + "..."
                    : _releaseNotes ?? "";
                _lblStatus.ForeColor = GreenColor;
                _lblStatus.Text = "✓ A new update is available!";
            }
            else if (!string.IsNullOrWhiteSpace(_downloadUrl))
            {
                // Same or older but still allow download
                _pnlServerInfo.Visible = true;
                _lblServerVer.Text = $"Latest Version: v{_latestVersion}";
                _lblReleaseNotes.Text = _releaseNotes?.Length > 120
                    ? _releaseNotes[..120] + "..."
                    : _releaseNotes ?? "";
                _lblStatus.ForeColor = GreenColor;
                _lblStatus.Text = "✓ You are up to date. You can still re-download if needed.";
            }
            else
            {
                _lblStatus.Text = "No downloadable .zip asset found in the latest release.";
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _lblStatusDot.ForeColor = DimText;
            _lblStatus.ForeColor = DimText;
            _lblStatus.Text = "No GitHub release has been published yet.";
        }
        catch (Exception ex)
        {
            _lblStatusDot.ForeColor = RedColor;
            _lblStatus.ForeColor = RedColor;
            _lblStatus.Text = $"Error checking for updates: {ex.Message}";
        }
        finally
        {
            _btnCheck.Enabled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // DOWNLOAD & INSTALL FROM GITHUB
    // ═══════════════════════════════════════════════════════════════
    private async Task DownloadAndInstall()
    {
        if (string.IsNullOrWhiteSpace(_downloadUrl)) return;

        _btnDownload.Enabled = false;
        _btnCheck.Enabled = false;
        _progress.Visible = true;
        _lblProgress.Visible = true;
        _lblProgress.Text = "Closing HISAB KITAB...";
        _progress.Value = 0;

        try
        {
            await WaitForMainAppToCloseAsync();
            _lblProgress.Text = "Downloading update...";

            // Download zip
            var fileName = Path.GetFileName(new Uri(_downloadUrl).LocalPath);
            var downloadPath = Path.Combine(
                Path.GetTempPath(),
                $"HISAB_KITAB_Update_{Guid.NewGuid():N}_{fileName}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("User-Agent", "HisabKitab-Updater");

            using var response = await client.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            long totalRead = 0;
            await using (var fileStream = new FileStream(
                             downloadPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                    {
                        var pct = (int)(totalRead * 100 / totalBytes);
                        _progress.Value = Math.Min(pct, 100);
                        _lblProgress.Text =
                            $"Downloading... {pct}% ({totalRead / 1024:N0} KB / {totalBytes / 1024:N0} KB)";
                    }
                }

                await fileStream.FlushAsync();
            }

            if (totalRead < 1024)
                throw new InvalidOperationException("The downloaded update package is incomplete.");

            _progress.Value = 100;
            _lblProgress.Text = "Download complete. Installing update...";

            // Apply update
            var installDir = !string.IsNullOrWhiteSpace(_appExe)
                ? Path.GetDirectoryName(_appExe)!
                : AppContext.BaseDirectory;

            void Log(string msg)
            {
                try
                {
                    var logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Hisab Kitab", "Logs", "update_log.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Updater GUI] {msg}\n");
                }
                catch { }
            }

            Program.ApplyZipUpdate(downloadPath, installDir, Log);

            _lblProgress.Text = "Update applied successfully!";
            _lblStatus.ForeColor = GreenColor;
            _lblStatus.Text = $"✓ Updated to v{_latestVersion}";

            // Ask to relaunch
            var result = MessageBox.Show(
                $"Update to v{_latestVersion} installed successfully!\n\nLaunch HISAB KITAB now?",
                "Update Complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result == DialogResult.Yes && !string.IsNullOrWhiteSpace(_appExe) && File.Exists(_appExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _appExe,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(_appExe)!
                });
            }

            Close();
        }
        catch (Exception ex)
        {
            _lblProgress.Text = "";
            _progress.Visible = false;
            _lblProgress.Visible = false;
            _lblStatus.ForeColor = RedColor;
            _lblStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _btnDownload.Enabled = true;
            _btnCheck.Enabled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // LOCAL FILE UPDATE
    // ═══════════════════════════════════════════════════════════════
    private void BrowseLocal()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Update Package",
            Filter = "Update Files (*.zip)|*.zip|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtFilePath.Text = dlg.FileName;
            _btnApplyLocal.Enabled = true;
        }
    }

    private void ApplyLocal()
    {
        var filePath = _txtFilePath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        var confirm = MessageBox.Show(
            $"Apply update from:\n{Path.GetFileName(filePath)}\n\nThis will close HISAB KITAB and apply the update.",
            "Confirm Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try
        {
            CloseMainApp();
            Thread.Sleep(2000);

            var installDir = !string.IsNullOrWhiteSpace(_appExe)
                ? Path.GetDirectoryName(_appExe)!
                : AppContext.BaseDirectory;

            Program.ApplyZipUpdate(filePath, installDir);

            _lblStatus.ForeColor = GreenColor;
            _lblStatus.Text = "✓ Local update applied successfully!";

            var result = MessageBox.Show("Update applied!\n\nLaunch HISAB KITAB now?",
                "Update Complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result == DialogResult.Yes && !string.IsNullOrWhiteSpace(_appExe) && File.Exists(_appExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _appExe,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(_appExe)!
                });
            }

            Close();
        }
        catch (Exception ex)
        {
            _lblStatus.ForeColor = RedColor;
            _lblStatus.Text = $"Error: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════
    private void CloseMainApp()
    {
        if (string.IsNullOrWhiteSpace(_appExe)) return;
        var name = Path.GetFileNameWithoutExtension(_appExe);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(1500))
                {
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }

    private async Task WaitForMainAppToCloseAsync()
    {
        if (_originatingProcessId is int pid && pid != Environment.ProcessId)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    _lblProgress.Text = "Closing HISAB KITAB...";
                    process.CloseMainWindow();
                    var exited = await Task.Run(() => process.WaitForExit(3000));
                    if (!exited)
                    {
                        _lblProgress.Text = "Finishing application shutdown...";
                        process.Kill(true);
                        await Task.Run(() => process.WaitForExit(5000));
                    }
                }
            }
            catch (ArgumentException)
            {
                // The originating application has already closed.
            }
        }

        CloseMainApp();
        await Task.Delay(300);
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var vLatest) && Version.TryParse(current, out var vCurrent))
            return vLatest > vCurrent;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static Panel CreateCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgCard,
            Margin = Padding.Empty
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(
                pen,
                0,
                0,
                Math.Max(0, card.Width - 1),
                Math.Max(0, card.Height - 1));
        };
        return card;
    }

    private static Button CreateButton(string text, Color bgColor, bool filled)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = bgColor,
            ForeColor = filled ? Color.White : Navy,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = filled
            ? (bgColor == Gold ? OrangeDark : GreenColor)
            : Blue;
        btn.FlatAppearance.MouseOverBackColor = filled
            ? (bgColor == Gold ? Color.FromArgb(224, 118, 22) : Color.FromArgb(35, 135, 73))
            : Color.FromArgb(235, 243, 252);
        btn.FlatAppearance.MouseDownBackColor = filled
            ? (bgColor == Gold ? OrangeDark : Color.FromArgb(28, 112, 61))
            : Color.FromArgb(222, 235, 249);
        return btn;
    }
}
