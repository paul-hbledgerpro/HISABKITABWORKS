using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal enum AppUpdateChoice
{
    UpdateNow,
    UpdateLater
}

internal sealed record AvailableAppUpdate(
    string Version,
    string DownloadUrl,
    string ReleaseNotes);

internal static class AppUpdateStartupService
{
    private const string GitHubLatestReleaseApi =
        "https://api.github.com/repos/paul-hbledgerpro/HISABKITABWORKS/releases/latest";
    private const string PreferredAssetPrefix = "HISAB_KITAB_Update_win-x64";
    private const int MaximumDeferrals = 3;
    private static readonly byte[] StateEntropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-APP-UPDATE-DEFERRALS-V1");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string StatePath =>
        Path.Combine(AppBootstrap.AppDataPath, "app-update-state.protected");

    public static async Task CheckAtStartupAsync(Form owner)
        => await CheckAsync(owner, showUpToDateMessage: false);

    public static async Task CheckManuallyAsync(Form owner)
        => await CheckAsync(owner, showUpToDateMessage: true);

    private static async Task CheckAsync(Form owner, bool showUpToDateMessage)
    {
        if (!await Gate.WaitAsync(0))
            return;
        try
        {
            var update = await FindUpdateAsync();
            if (update is null)
            {
                ClearState();
                if (showUpToDateMessage)
                    MessageBox.Show(
                        owner,
                        $"HISAB KITAB {GetCurrentVersion()} is up to date.",
                        "Software Update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                return;
            }

            var state = LoadState(update.Version);
            var forced = state.DeferralCount >= MaximumDeferrals;
            if (forced)
            {
                MessageBox.Show(
                    owner,
                    $"HISAB KITAB {update.Version} is now required.\n\n" +
                    "This update was postponed three times and will be installed now. " +
                    "The application will restart automatically.",
                    "Required Software Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                await DownloadAndLaunchUpdaterAsync(owner, update, required: true);
                return;
            }

            using var prompt = new AppUpdatePromptForm(
                update,
                state.DeferralCount,
                MaximumDeferrals);
            var choice = prompt.ShowDialog(owner) == DialogResult.OK
                ? AppUpdateChoice.UpdateNow
                : AppUpdateChoice.UpdateLater;
            if (choice == AppUpdateChoice.UpdateNow)
            {
                await DownloadAndLaunchUpdaterAsync(owner, update, required: false);
                return;
            }

            state.DeferralCount++;
            state.LastDeferredUtc = DateTime.UtcNow;
            SaveState(state);
        }
        catch (HttpRequestException)
        {
            // Startup must remain usable when the PC is offline or GitHub is unavailable.
            if (showUpToDateMessage)
                MessageBox.Show(
                    owner,
                    "HISAB KITAB could not reach the update server. Please check the internet connection and try again.",
                    "Software Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
        }
        catch (TaskCanceledException)
        {
            if (showUpToDateMessage)
                MessageBox.Show(
                    owner,
                    "The update check timed out. Please try again.",
                    "Software Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            if (showUpToDateMessage)
                MessageBox.Show(
                    owner,
                    $"The update check could not be completed.\n\n{AppBootstrap.RedactSensitiveText(ex.Message)}",
                    "Software Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<AvailableAppUpdate?> FindUpdateAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HisabKitabWorks", GetCurrentVersion()));
        using var response = await client.GetAsync(GitHubLatestReleaseApi);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var release = await JsonDocument.ParseAsync(stream);
        var root = release.RootElement;
        var latestVersion = NormalizeVersion(root.GetProperty("tag_name").GetString());
        if (!IsNewer(latestVersion, GetCurrentVersion()))
            return null;

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            var candidates = assets.EnumerateArray()
                .Select(asset => new
                {
                    Name = asset.GetProperty("name").GetString() ?? "",
                    Url = asset.GetProperty("browser_download_url").GetString() ?? ""
                })
                .Where(asset =>
                    asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(asset.Url))
                .ToList();
            downloadUrl = candidates
                .FirstOrDefault(asset =>
                    asset.Name.StartsWith(PreferredAssetPrefix, StringComparison.OrdinalIgnoreCase))?.Url
                ?? candidates.FirstOrDefault()?.Url;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            return null;
        var notes = root.TryGetProperty("body", out var body)
            ? body.GetString() ?? ""
            : "";
        return new AvailableAppUpdate(latestVersion, downloadUrl, notes);
    }

    private static Task DownloadAndLaunchUpdaterAsync(
        Form owner,
        AvailableAppUpdate update,
        bool required)
    {
        var updaterPath = Path.Combine(AppContext.BaseDirectory, "Upgrade.exe");
        if (!File.Exists(updaterPath))
        {
            MessageBox.Show(
                owner,
                "Upgrade.exe is missing from the HISAB KITAB installation. " +
                "Please reinstall the latest client setup package.",
                "Software Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return Task.CompletedTask;
        }

        using var download = new AppUpdateDownloadForm(update, required);
        if (download.ShowDialog(owner) != DialogResult.OK ||
            string.IsNullOrWhiteSpace(download.DownloadedPackagePath))
            return Task.CompletedTask;

        var appExe = Application.ExecutablePath;
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--zip");
        startInfo.ArgumentList.Add(download.DownloadedPackagePath);
        startInfo.ArgumentList.Add("--app");
        startInfo.ArgumentList.Add(appExe);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The updater could not be started.");

        owner.Hide();
        owner.BeginInvoke(new Action(Application.Exit));
        return Task.CompletedTask;
    }

    private static AppUpdateDeferralState LoadState(string updateVersion)
    {
        try
        {
            if (!File.Exists(StatePath))
                return NewState(updateVersion);
            var protectedBytes = File.ReadAllBytes(StatePath);
            var clear = ProtectedData.Unprotect(
                protectedBytes,
                StateEntropy,
                DataProtectionScope.LocalMachine);
            try
            {
                var state = JsonSerializer.Deserialize<AppUpdateDeferralState>(clear, JsonOptions);
                if (state is null ||
                    !string.Equals(state.UpdateVersion, updateVersion, StringComparison.OrdinalIgnoreCase))
                    return NewState(updateVersion);
                state.DeferralCount = Math.Clamp(state.DeferralCount, 0, MaximumDeferrals);
                return state;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return NewState(updateVersion);
        }
    }

    private static void SaveState(AppUpdateDeferralState state)
    {
        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        var clear = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                clear,
                StateEntropy,
                DataProtectionScope.LocalMachine);
            var temporaryPath = StatePath + ".new";
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, StatePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static void ClearState()
    {
        try
        {
            if (File.Exists(StatePath))
                File.Delete(StatePath);
        }
        catch
        {
            // A stale counter is harmless; it is scoped to a specific release version.
        }
    }

    private static AppUpdateDeferralState NewState(string version) => new()
    {
        UpdateVersion = version,
        DeferralCount = 0
    };

    private static string GetCurrentVersion()
    {
        try
        {
            var versionPath = Path.Combine(AppContext.BaseDirectory, "version.txt");
            if (File.Exists(versionPath))
            {
                var fileVersion = NormalizeVersion(File.ReadAllText(versionPath));
                if (!string.IsNullOrWhiteSpace(fileVersion))
                    return fileVersion;
            }
        }
        catch
        {
            // Fall back to assembly metadata.
        }

        return NormalizeVersion(
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)) is { Length: > 0 } version
            ? version
            : "0.0.0";
    }

    private static string NormalizeVersion(string? value)
        => (value ?? "")
            .Trim()
            .TrimStart('v', 'V')
            .Split(new[] { '-', '+' }, 2)[0];

    private static bool IsNewer(string available, string current)
        => Version.TryParse(available, out var availableVersion) &&
           Version.TryParse(NormalizeVersion(current), out var currentVersion) &&
           availableVersion > currentVersion;

    private sealed class AppUpdateDeferralState
    {
        public string UpdateVersion { get; set; } = "";
        public int DeferralCount { get; set; }
        public DateTime? LastDeferredUtc { get; set; }
    }
}

internal sealed class AppUpdatePromptForm : Form
{
    public AppUpdatePromptForm(
        AvailableAppUpdate update,
        int previousDeferrals,
        int maximumDeferrals)
    {
        WinTheme.Apply(this);
        Text = "HISAB KITAB - Software Update";
        Icon = WinTheme.TryLoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(660, 390);
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            BackColor = WinTheme.Bg,
            Padding = new Padding(24)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = $"SOFTWARE UPDATE {update.Version} IS AVAILABLE",
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(16),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = previousDeferrals == 0
                ? "Install now for the latest improvements and fixes."
                : $"This update has been postponed {previousDeferrals} of {maximumDeferrals} times. " +
                  "After three postponements, it will install automatically on the next startup.",
            ForeColor = WinTheme.BlueDark,
            Font = WinTheme.BodyFont(10.5f),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        root.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            ForeColor = WinTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = WinTheme.BodyFont(9.5f),
            Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                ? "This release contains application improvements and fixes."
                : update.ReleaseNotes
        }, 0, 2);
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = WinTheme.Panel,
            Padding = new Padding(0, 10, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var later = WinTheme.Button("UPDATE LATER");
        later.Dock = DockStyle.Fill;
        later.Margin = new Padding(0, 0, 8, 0);
        later.DialogResult = DialogResult.Cancel;
        var now = WinTheme.Button("UPDATE NOW", true);
        now.Dock = DockStyle.Fill;
        now.Margin = new Padding(8, 0, 0, 0);
        now.DialogResult = DialogResult.OK;
        actions.Controls.Add(later, 0, 0);
        actions.Controls.Add(now, 1, 0);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        AcceptButton = now;
        CancelButton = later;
    }
}

internal sealed class AppUpdateDownloadForm : Form
{
    private readonly AvailableAppUpdate _update;
    private readonly Label _status;
    private readonly ProgressBar _progress;

    public AppUpdateDownloadForm(AvailableAppUpdate update, bool required)
    {
        _update = update;
        WinTheme.Apply(this);
        Text = required ? "Installing Required Update" : "Downloading Software Update";
        Icon = WinTheme.TryLoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(560, 190);
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            BackColor = WinTheme.Bg,
            Padding = new Padding(24)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = $"PREPARING HISAB KITAB {update.Version}",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(14),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        root.Controls.Add(_progress, 0, 1);
        _status = new Label
        {
            Text = "Connecting to the update server...",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.BlueDark,
            Font = WinTheme.BodyFont(10),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
        Shown += async (_, _) => await DownloadAsync();
    }

    public string? DownloadedPackagePath { get; private set; }

    private async Task DownloadAsync()
    {
        try
        {
            var destination = Path.Combine(
                Path.GetTempPath(),
                $"HISAB_KITAB_Update_{_update.Version}_{Guid.NewGuid():N}.zip");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HisabKitabWorks", "1.0"));
            using var response = await client.GetAsync(
                _update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[81920];
            long readTotal = 0;
            await using (var output = new FileStream(
                             destination,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    readTotal += read;
                    if (length is > 0)
                        _progress.Value = Math.Clamp((int)(readTotal * 100 / length.Value), 0, 100);
                    _status.Text = length is > 0
                        ? $"Downloading... {_progress.Value}%"
                        : $"Downloading... {readTotal / 1024:N0} KB";
                }
                await output.FlushAsync();
            }

            if (readTotal < 1024)
                throw new InvalidOperationException("The downloaded update package is incomplete.");
            var signature = new byte[4];
            await using (var verify = File.OpenRead(destination))
                _ = await verify.ReadAsync(signature);
            if (signature[0] != (byte)'P' || signature[1] != (byte)'K')
                throw new InvalidOperationException("The downloaded file is not a valid update package.");
            DownloadedPackagePath = destination;
            _progress.Value = 100;
            _status.Text = "Download complete. HISAB KITAB will close, update, and restart.";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = $"Update download failed: {AppBootstrap.RedactSensitiveText(ex.Message)}";
            MessageBox.Show(
                this,
                _status.Text,
                "Software Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            DialogResult = DialogResult.Abort;
            Close();
        }
    }
}
