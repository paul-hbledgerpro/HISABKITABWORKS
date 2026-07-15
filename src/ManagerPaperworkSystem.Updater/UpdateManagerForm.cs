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
/// Navy and copper themed to match HISAB KITAB.
/// </summary>
public class UpdateManagerForm : Form
{
    // GitHub config — same as main app
    private const string GitHubOwner = "paul-hbledgerpro";
    private const string GitHubRepo = "HISABKITABWORKS";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    private const string PreferredAssetPrefix = "HISAB_KITAB_Update_win-x64";

    // Colors
    private static readonly Color Gold = Color.FromArgb(212, 175, 55);
    private static readonly Color BgDark = Color.FromArgb(11, 11, 15);
    private static readonly Color BgCard = Color.FromArgb(19, 19, 26);
    private static readonly Color BgField = Color.FromArgb(26, 26, 34);
    private static readonly Color BorderColor = Color.FromArgb(34, 34, 48);
    private static readonly Color DimText = Color.FromArgb(138, 138, 154);
    private static readonly Color GreenColor = Color.FromArgb(34, 197, 94);
    private static readonly Color RedColor = Color.FromArgb(239, 68, 68);

    private readonly string? _appExe;
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

    public UpdateManagerForm(string? appExe)
    {
        _appExe = appExe;
        DetectCurrentVersion();
        InitializeUI();
        Shown += async (_, _) => await CheckForUpdates();
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
        Size = new Size(620, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BgDark;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);

        // ── Header bar ──
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top, Height = 70,
            BackColor = Color.FromArgb(17, 17, 24)
        };
        headerPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(Gold, 2);
            e.Graphics.DrawLine(pen, 0, headerPanel.Height - 1, headerPanel.Width, headerPanel.Height - 1);
        };

        _lblTitle = new Label
        {
            Text = "HISAB KITAB",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Gold,
            Location = new Point(20, 10), AutoSize = true
        };

        var lblSub = new Label
        {
            Text = "UPDATE MANAGER",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = DimText,
            Location = new Point(22, 42), AutoSize = true
        };

        _lblStatusDot = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 12),
            ForeColor = DimText,
            Location = new Point(520, 25), AutoSize = true
        };

        headerPanel.Controls.AddRange(new Control[] { _lblTitle, lblSub, _lblStatusDot });
        Controls.Add(headerPanel);

        var y = 85;

        // ── Current version ──
        _lblCurrentVer = CreateLabel($"Current Version: {_currentVersion}", 20, y, DimText);
        y += 30;

        // ── Status ──
        _lblStatus = CreateLabel("Checking GitHub for updates automatically...", 20, y, DimText);
        _lblStatus.MaximumSize = new Size(560, 0);
        _lblStatus.AutoSize = true;
        y += 30;

        // ── Check button ──
        _btnCheck = CreateButton("🔍  Check for Updates", 20, y, 250, 42, Gold);
        _btnCheck.Click += async (s, e) => await CheckForUpdates();
        y += 55;

        // ── Server info panel (hidden until check succeeds) ──
        _pnlServerInfo = new Panel
        {
            Location = new Point(20, y), Size = new Size(560, 120),
            BackColor = BgCard, Visible = false
        };
        _pnlServerInfo.Paint += (s, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(pen, 0, 0, _pnlServerInfo.Width - 1, _pnlServerInfo.Height - 1);
        };

        _lblServerVer = new Label
        {
            Text = "New Version: —",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = GreenColor,
            Location = new Point(14, 10), AutoSize = true
        };

        _lblReleaseNotes = new Label
        {
            Text = "",
            ForeColor = Color.White,
            Location = new Point(14, 34),
            Size = new Size(530, 36), AutoSize = false
        };

        _btnDownload = CreateButton("⬇  Download & Install Update", 14, 78, 530, 34, GreenColor);
        _btnDownload.Click += async (s, e) => await DownloadAndInstall();

        _pnlServerInfo.Controls.AddRange(new Control[] { _lblServerVer, _lblReleaseNotes, _btnDownload });
        Controls.Add(_pnlServerInfo);
        y += 130;

        // ── Progress ──
        _progress = new ProgressBar
        {
            Location = new Point(20, y), Size = new Size(560, 24),
            Style = ProgressBarStyle.Continuous, Visible = false
        };
        Controls.Add(_progress);

        _lblProgress = CreateLabel("", 20, y + 28, DimText);
        _lblProgress.Visible = false;
        y += 60;

        // ── Separator ──
        var sep = new Panel { Location = new Point(20, y), Size = new Size(560, 1), BackColor = BorderColor };
        Controls.Add(sep);
        y += 16;

        // ── Local file section ──
        var lblLocal = CreateLabel("APPLY LOCAL UPDATE FILE", 20, y, Gold, true);
        y += 24;

        _txtFilePath = new TextBox
        {
            Location = new Point(20, y), Size = new Size(430, 30),
            BackColor = BgField, ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true, Text = "No file selected...",
            Font = new Font("Segoe UI", 9.5f)
        };
        Controls.Add(_txtFilePath);

        _btnBrowse = CreateButton("Browse...", 458, y, 122, 30, BorderColor);
        _btnBrowse.ForeColor = DimText;
        _btnBrowse.FlatAppearance.BorderColor = BorderColor;
        _btnBrowse.Click += (s, e) => BrowseLocal();
        y += 40;

        _btnApplyLocal = CreateButton("📁  Apply Local Update", 20, y, 250, 38, Gold);
        _btnApplyLocal.Enabled = false;
        _btnApplyLocal.Click += (s, e) => ApplyLocal();
        y += 55;

        // ── Close button ──
        _btnClose = CreateButton("Close", 480, y, 100, 34, BorderColor);
        _btnClose.ForeColor = DimText;
        _btnClose.FlatAppearance.BorderColor = BorderColor;
        _btnClose.Click += (s, e) => Close();
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
        _lblProgress.Text = "Downloading...";
        _progress.Value = 0;

        try
        {
            // Download zip
            var fileName = Path.GetFileName(new Uri(_downloadUrl).LocalPath);
            var downloadPath = Path.Combine(Path.GetTempPath(), $"HisabKitab_{fileName}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("User-Agent", "HisabKitab-Updater");

            using var response = await client.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var pct = (int)(totalRead * 100 / totalBytes);
                    _progress.Value = Math.Min(pct, 100);
                    _lblProgress.Text = $"Downloading... {pct}% ({totalRead / 1024}KB / {totalBytes / 1024}KB)";
                }
            }

            fileStream.Close();
            _progress.Value = 100;
            _lblProgress.Text = "Download complete. Closing main app and applying...";

            // Close main app
            CloseMainApp();
            Thread.Sleep(2000);

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
                if (!p.WaitForExit(5000)) p.Kill(true);
            }
            catch { }
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var vLatest) && Version.TryParse(current, out var vCurrent))
            return vLatest > vCurrent;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private Label CreateLabel(string text, int x, int y, Color color, bool bold = false)
    {
        var lbl = new Label
        {
            Text = text,
            ForeColor = color,
            Location = new Point(x, y),
            AutoSize = true,
            Font = bold ? new Font("Segoe UI", 10, FontStyle.Bold) : new Font("Segoe UI", 9.5f)
        };
        Controls.Add(lbl);
        return lbl;
    }

    private Button CreateButton(string text, int x, int y, int w, int h, Color bgColor)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = bgColor,
            ForeColor = bgColor == Gold || bgColor == GreenColor ? Color.Black : Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        Controls.Add(btn);
        return btn;
    }
}
