using System.Diagnostics;
using System.Net.Http.Headers;
using System.IO.Compression;
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
    string? DownloadUrl,
    string ReleaseNotes,
    string? StagedArtifactPath = null,
    string? CentriqResultPath = null,
    Guid? CentriqOperationId = null);

internal static class AppUpdateStartupService
{
    private const string GitHubLatestReleaseApi =
        "https://api.github.com/repos/paul-hbledgerpro/HISABKITABWORKS/releases/latest";
    private const string PreferredAssetPrefix = "HISAB_KITAB_Update_win-x64";
    private const int MaximumDeferrals = 3;
    private const string CentriqProductCode = "HISAB-KITAB";
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

    public static string CurrentVersion => GetCurrentVersion();

    public static async Task<bool> CheckBeforeApplicationStartupAsync()
        => await CheckAsync(owner: null, showUpToDateMessage: false);

    public static async Task CheckAtStartupAsync(Form owner)
        => _ = await CheckAsync(owner, showUpToDateMessage: false);

    public static async Task CheckManuallyAsync(Form owner)
        => _ = await CheckAsync(owner, showUpToDateMessage: true);

    private static async Task<bool> CheckAsync(Form? owner, bool showUpToDateMessage)
    {
        if (!await Gate.WaitAsync(0))
            return false;
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
                return false;
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
                return await DownloadAndLaunchUpdaterAsync(owner, update, required: true);
            }

            using var prompt = new AppUpdatePromptForm(
                update,
                state.DeferralCount,
                MaximumDeferrals);
            var promptResult = owner is null
                ? prompt.ShowDialog()
                : prompt.ShowDialog(owner);
            var choice = promptResult == DialogResult.OK
                ? AppUpdateChoice.UpdateNow
                : AppUpdateChoice.UpdateLater;
            if (choice == AppUpdateChoice.UpdateNow)
            {
                return await DownloadAndLaunchUpdaterAsync(owner, update, required: false);
            }

            state.DeferralCount++;
            state.LastDeferredUtc = DateTime.UtcNow;
            SaveState(state);
            return false;
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
            return false;
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
            return false;
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
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<AvailableAppUpdate?> FindUpdateAsync()
    {
        var staged = FindCentriqStagedUpdate();
        if (staged is not null)
            return staged;

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

    private static AvailableAppUpdate? FindCentriqStagedUpdate()
    {
        var pendingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Centriq",
            "Updates",
            CentriqProductCode,
            "pending-update.json");
        if (!File.Exists(pendingPath)) return null;
        var productUpdateRoot = Path.GetFullPath(Path.GetDirectoryName(pendingPath)!);

        using var document = JsonDocument.Parse(File.ReadAllText(pendingPath));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) return null;
        var productCode = root.GetProperty("productCode").GetString();
        var version = NormalizeVersion(root.GetProperty("targetVersion").GetString());
        var installRoot = Path.GetFullPath(root.GetProperty("installRoot").GetString() ?? "");
        var expectedRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var artifactPath = Path.GetFullPath(root.GetProperty("artifactPath").GetString() ?? "");
        var resultPath = Path.GetFullPath(root.GetProperty("resultPath").GetString() ?? "");
        var operationId = root.GetProperty("operationId").GetGuid();
        if (!string.Equals(productCode, CentriqProductCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                installRoot.TrimEnd(Path.DirectorySeparatorChar),
                expectedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(version, NormalizeVersion(GetCurrentVersion()), StringComparison.OrdinalIgnoreCase)
            || operationId == Guid.Empty
            || !IsPathBelow(productUpdateRoot, artifactPath)
            || !IsPathBelow(productUpdateRoot, resultPath)
            || !File.Exists(artifactPath))
            return null;

        return new AvailableAppUpdate(
            version,
            null,
            "CENTRIQ TECH assigned this signed update specifically to this computer. " +
            "Choose Update now to install it, or Update later to be reminded the next time HISAB KITAB opens.",
            artifactPath,
            resultPath,
            operationId);
    }

    private static bool IsPathBelow(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> DownloadAndLaunchUpdaterAsync(
        Form? owner,
        AvailableAppUpdate update,
        bool required)
    {
        // Bootstrap the updater from the release being installed. Older client
        // folders can contain a leftover UpdaterPayload\Upgrade.exe whose
        // manifest requests administrator elevation. Selecting that stale file
        // caused UAC prompts and then relaunched the app in the administrator's
        // Windows profile, where the normal user's license is intentionally not
        // present. The release ZIP contains the matching asInvoker updater, so
        // download it once and run that exact binary as the current user.
        var prepared = await PrepareReleaseUpdaterAsync(update);
        var updaterWorkingDirectory = prepared.WorkingDirectory;
        var updaterPath = Path.Combine(updaterWorkingDirectory, "Upgrade.exe");
        var appExe = Application.ExecutablePath;
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = updaterWorkingDirectory,
            // CreateProcess never displays a credential/UAC prompt. If a future
            // package accidentally ships an elevated updater, startup fails
            // safely instead of switching Windows accounts and losing sight of
            // the existing per-user license.
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--zip");
        startInfo.ArgumentList.Add(prepared.PackagePath);
        if (!string.IsNullOrWhiteSpace(update.StagedArtifactPath))
        {
            startInfo.ArgumentList.Add("--centriq-result");
            startInfo.ArgumentList.Add(update.CentriqResultPath!);
            startInfo.ArgumentList.Add("--centriq-operation");
            startInfo.ArgumentList.Add(update.CentriqOperationId!.Value.ToString("D"));
        }
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(update.Version);
        startInfo.ArgumentList.Add("--app");
        startInfo.ArgumentList.Add(appExe);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--required");
        startInfo.ArgumentList.Add(required ? "true" : "false");
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The updater could not be started.");

        if (owner is not null)
        {
            owner.Hide();
            owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    owner.Close();
                }
                finally
                {
                    Application.Exit();
                }
            }));
        }

        return true;
    }

    private static async Task<PreparedReleaseUpdater> PrepareReleaseUpdaterAsync(
        AvailableAppUpdate update)
    {
        var updaterRoot = Path.Combine(Path.GetTempPath(), "HISAB_KITAB_UPDATER");
        Directory.CreateDirectory(updaterRoot);
        var workingDirectory = Path.Combine(
            updaterRoot,
            $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        var packagePath = Path.Combine(workingDirectory, "update.zip");

        if (!string.IsNullOrWhiteSpace(update.StagedArtifactPath))
        {
            File.Copy(update.StagedArtifactPath, packagePath, overwrite: false);
        }
        else
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HisabKitabWorks", GetCurrentVersion()));
            await using var source = await client.GetStreamAsync(update.DownloadUrl);
            await using var destination = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            await source.CopyToAsync(destination);
        }

        using (var archive = ZipFile.OpenRead(packagePath))
        {
            var updaterEntry = archive.Entries
                .Where(entry => string.Equals(
                    Path.GetFileName(entry.FullName),
                    "Upgrade.exe",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName.Count(character => character is '/' or '\\'))
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    "The release package does not contain Upgrade.exe.");
            updaterEntry.ExtractToFile(
                Path.Combine(workingDirectory, "Upgrade.exe"),
                overwrite: true);
        }

        var updaterPath = Path.Combine(workingDirectory, "Upgrade.exe");
        var updaterVersion = NormalizeVersion(
            FileVersionInfo.GetVersionInfo(updaterPath).FileVersion);
        if (!Version.TryParse(updaterVersion, out var parsedUpdaterVersion) ||
            !Version.TryParse(NormalizeVersion(update.Version), out var parsedReleaseVersion) ||
            parsedUpdaterVersion < parsedReleaseVersion)
        {
            throw new InvalidDataException(
                $"The release updater version {updaterVersion} does not match update {update.Version}.");
        }

        var sourceDescription = string.IsNullOrWhiteSpace(update.StagedArtifactPath)
            ? update.DownloadUrl
            : "a Centriq-assigned staged package";
        WriteUpdateDiagnostic(
            $"Prepared release updater {updaterVersion} from {sourceDescription} for Windows user {Environment.UserName}.");
        return new PreparedReleaseUpdater(workingDirectory, packagePath);
    }

    private static void WriteUpdateDiagnostic(string message)
    {
        try
        {
            var logDirectory = Path.Combine(AppBootstrap.AppDataPath, "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "update_log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Application] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never block application startup.
        }
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

    private sealed record PreparedReleaseUpdater(
        string WorkingDirectory,
        string PackagePath);
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
