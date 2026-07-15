using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ManagerPaperworkSystem.UI.Services;

/// <summary>
/// Handles app updates — supports local file, network share, and remote server updates.
/// Master password is required for update access (programmer-only feature).
/// </summary>
public class UpdateService
{
    // ═══════════════════════════════════════════════════════════════
    // VERSION — auto-read from project AssemblyVersion
    // To bump: change <Version> in ManagerPaperworkSystem.UI.csproj
    // ═══════════════════════════════════════════════════════════════
    public static readonly string CurrentVersion = 
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Master password hash (SHA256 of "$Diploma4u")
    private const string MasterPasswordHash = "B88790CDC75A7748139DBE89E03D724E3A8AEBE537BD9C5CDEEAD9B1E1E3FE14";

    // Remote update server — GitHub Releases
    // Format: https://api.github.com/repos/{owner}/{repo}/releases/latest
    // Change these to your GitHub repo:
    private const string GitHubOwner = "paul-hbledgerpro";  // Your GitHub username
    private const string GitHubRepo = "HBStoreLedgerPro";    // Your repo name
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    // Local paths
    private static readonly string AppInstallDir = AppContext.BaseDirectory;
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab");
    private static readonly string BackupDir = Path.Combine(AppDataDir, "update_backup");
    private static readonly string UpdateLogPath = Path.Combine(AppDataDir, "Logs", "update_log.txt");

    // ═══════════════════════════════════════════════════════════════
    // PASSWORD VERIFICATION
    // ═══════════════════════════════════════════════════════════════
    public static bool VerifyMasterPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var inputHash = ComputeSha256Hash(password);
        return string.Equals(inputHash, MasterPasswordHash, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════
    // CHECK FOR UPDATES (GitHub Releases)
    // ═══════════════════════════════════════════════════════════════
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            // GitHub API requires a User-Agent header
            client.DefaultRequestHeaders.Add("User-Agent", "HisabKitab-Updater");

            var response = await client.GetStringAsync(GitHubApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(response);

            var tagName = release.GetProperty("tag_name").GetString() ?? "";
            var releaseName = release.GetProperty("name").GetString() ?? "";
            var body = release.GetProperty("body").GetString() ?? "";
            var prerelease = release.GetProperty("prerelease").GetBoolean();

            // Extract version number from tag (e.g., "v1.0.69" → "1.0.69")
            var latestVersion = tagName.TrimStart('v', 'V');

            // Compare versions
            var isNewer = IsVersionNewer(latestVersion, CurrentVersion);

            // Find the .zip or .mpsupdate asset download URL
            string? downloadUrl = null;
            if (release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.GetProperty("name").GetString() ?? "";
                    if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        assetName.EndsWith(".mpsupdate", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            return new UpdateCheckResult
            {
                IsUpdateAvailable = isNewer,
                LatestVersion = latestVersion,
                DownloadUrl = downloadUrl,
                ReleaseNotes = $"{releaseName}\n\n{body}",
                ErrorMessage = isNewer && string.IsNullOrEmpty(downloadUrl) 
                    ? "Update available but no download package found in the release. Use local file update instead." 
                    : null
            };
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                ErrorMessage = $"Could not reach GitHub: {ex.Message}\n\nYou can still apply a local update file (.zip or .mpsupdate)."
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                ErrorMessage = $"Error checking for updates: {ex.Message}\n\nYou can still apply a local update file."
            };
        }
    }

    /// <summary>
    /// Compares two version strings (e.g., "1.0.69" > "1.0.68")
    /// </summary>
    private static bool IsVersionNewer(string latest, string current)
    {
        try
        {
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();
            var currentParts = current.Split('.').Select(int.Parse).ToArray();
            var len = Math.Max(latestParts.Length, currentParts.Length);

            for (int i = 0; i < len; i++)
            {
                var l = i < latestParts.Length ? latestParts[i] : 0;
                var c = i < currentParts.Length ? currentParts[i] : 0;
                if (l > c) return true;
                if (l < c) return false;
            }
            return false; // equal
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // APPLY LOCAL UPDATE (from .zip or .mpsupdate file)
    // ═══════════════════════════════════════════════════════════════
    /// <summary>
    /// Applies an update from a local zip/mpsupdate file.
    /// Creates a backup of current files before overwriting.
    /// </summary>
    public static async Task<UpdateResult> ApplyLocalUpdateAsync(string packagePath, IProgress<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return new UpdateResult { Success = false, Message = "Update file not found." };
        }

        var ext = Path.GetExtension(packagePath).ToLowerInvariant();
        if (ext != ".zip" && ext != ".mpsupdate")
        {
            return new UpdateResult { Success = false, Message = "Invalid update file. Expected .zip or .mpsupdate file." };
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "HisabKitab_Update_" + Guid.NewGuid().ToString("N"));

        try
        {
            WriteLog($"Starting local update from: {packagePath}");
            progress?.Report(5);

            // Step 1: Extract to temp directory
            Directory.CreateDirectory(tempDir);
            progress?.Report(10);

            await Task.Run(() => ZipFile.ExtractToDirectory(packagePath, tempDir, overwriteFiles: true));
            progress?.Report(30);
            WriteLog($"Extracted to temp dir: {tempDir}");

            // Check what we extracted
            var extractedFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            if (extractedFiles.Length == 0)
            {
                return new UpdateResult { Success = false, Message = "Update package is empty." };
            }

            WriteLog($"Found {extractedFiles.Length} files in update package");

            // Step 2: Backup current files (only the ones being replaced)
            progress?.Report(40);
            Directory.CreateDirectory(BackupDir);
            var backupTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var thisBackupDir = Path.Combine(BackupDir, backupTimestamp);
            Directory.CreateDirectory(thisBackupDir);

            int backed = 0;
            foreach (var src in extractedFiles)
            {
                var rel = Path.GetRelativePath(tempDir, src);
                var dest = Path.Combine(AppInstallDir, rel);

                if (File.Exists(dest))
                {
                    var backupDest = Path.Combine(thisBackupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupDest)!);
                    File.Copy(dest, backupDest, overwrite: true);
                    backed++;
                }
            }
            WriteLog($"Backed up {backed} existing files to: {thisBackupDir}");
            progress?.Report(60);

            // Step 3: Copy new files into install directory
            int copied = 0;
            int failed = 0;
            var failedFiles = new System.Collections.Generic.List<string>();

            foreach (var src in extractedFiles)
            {
                var rel = Path.GetRelativePath(tempDir, src);
                var dest = Path.Combine(AppInstallDir, rel);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                    copied++;
                }
                catch (IOException ioEx)
                {
                    // File might be locked (e.g., the running .exe itself)
                    failedFiles.Add($"{rel}: {ioEx.Message}");
                    failed++;
                }
            }

            progress?.Report(90);
            WriteLog($"Copied {copied} files, {failed} failed");

            // Step 4: Clean up temp
            try { Directory.Delete(tempDir, recursive: true); } catch { }

            progress?.Report(100);

            // Build result message
            if (failed > 0)
            {
                var msg = $"Update partially applied: {copied} files updated, {failed} files could not be replaced (locked).\n\n" +
                          $"Locked files:\n{string.Join("\n", failedFiles)}\n\n" +
                          "These files will be updated on next restart. Restart now?";
                WriteLog($"Partial update: {msg}");
                return new UpdateResult { Success = true, Message = msg, RequiresRestart = true };
            }

            WriteLog("Update applied successfully");
            return new UpdateResult
            {
                Success = true,
                Message = $"Update applied successfully! {copied} files updated.\nBackup saved to: {thisBackupDir}\n\nRestart to use the new version.",
                RequiresRestart = true
            };
        }
        catch (Exception ex)
        {
            WriteLog($"Update failed: {ex}");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            return new UpdateResult { Success = false, Message = $"Update failed: {ex.Message}" };
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // ROLLBACK (restore from backup)
    // ═══════════════════════════════════════════════════════════════
    public static UpdateResult RollbackLastUpdate()
    {
        try
        {
            if (!Directory.Exists(BackupDir))
                return new UpdateResult { Success = false, Message = "No backup found." };

            var backups = Directory.GetDirectories(BackupDir)
                .OrderByDescending(d => d)
                .ToList();

            if (backups.Count == 0)
                return new UpdateResult { Success = false, Message = "No backup found." };

            var latestBackup = backups[0];
            var files = Directory.GetFiles(latestBackup, "*", SearchOption.AllDirectories);

            int restored = 0;
            foreach (var src in files)
            {
                var rel = Path.GetRelativePath(latestBackup, src);
                var dest = Path.Combine(AppInstallDir, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                    restored++;
                }
                catch { }
            }

            WriteLog($"Rolled back {restored} files from {latestBackup}");
            return new UpdateResult
            {
                Success = true,
                Message = $"Rolled back {restored} files. Restart the application.",
                RequiresRestart = true
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult { Success = false, Message = $"Rollback failed: {ex.Message}" };
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // DOWNLOAD FROM GITHUB RELEASES
    // ═══════════════════════════════════════════════════════════════
    public static async Task<UpdateResult> DownloadAndApplyUpdateAsync(string downloadUrl, IProgress<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return new UpdateResult { Success = false, Message = "No download URL available. Use local file update instead." };
        }

        try
        {
            progress?.Report(5);

            var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            var downloadPath = Path.Combine(Path.GetTempPath(), $"HisabKitab_{fileName}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("User-Agent", "HisabKitab-Updater");

            progress?.Report(10);

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (int)(10 + (totalRead * 50 / totalBytes));
                    progress?.Report(Math.Min(percent, 60));
                }
            }

            fileStream.Close();
            progress?.Report(60);

            // Apply the downloaded package
            var result = await ApplyLocalUpdateAsync(downloadPath, new Progress<int>(p =>
            {
                progress?.Report(60 + (p * 40 / 100));
            }));

            try { File.Delete(downloadPath); } catch { }
            return result;
        }
        catch (Exception ex)
        {
            return new UpdateResult
            {
                Success = false,
                Message = $"Download failed: {ex.Message}\n\nTry using a local update file instead."
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CREATE UPDATE PACKAGE (developer utility)
    // ═══════════════════════════════════════════════════════════════
    /// <summary>
    /// Creates an update package from a published output directory.
    /// Usage: Publish your app, then zip the output into .mpsupdate
    /// </summary>
    public static string CreateUpdatePackage(string publishDir, string outputPath)
    {
        if (!Directory.Exists(publishDir))
            throw new DirectoryNotFoundException($"Publish directory not found: {publishDir}");

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        ZipFile.CreateFromDirectory(publishDir, outputPath, CompressionLevel.Optimal, false);
        return outputPath;
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════
    private static string ComputeSha256Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        var builder = new StringBuilder();
        foreach (var b in bytes)
            builder.Append(b.ToString("X2"));
        return builder.ToString();
    }

    public static string GeneratePasswordHash(string password)
    {
        return ComputeSha256Hash(password);
    }

    private static void WriteLog(string message)
    {
        try
        {
            var logDir = Path.GetDirectoryName(UpdateLogPath)!;
            Directory.CreateDirectory(logDir);
            File.AppendAllText(UpdateLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════
    // EXTERNAL UPDATER FLOW
    // Downloads zip to temp, launches Update.exe, shuts down main app
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Downloads the update zip to a temp file and returns the path.
    /// </summary>
    public static async Task<string?> DownloadToTempAsync(string downloadUrl, IProgress<int>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        var downloadPath = Path.Combine(Path.GetTempPath(), $"HisabKitab_{fileName}");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Add("User-Agent", "HisabKitab-Updater");

        progress?.Report(5);

        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;
            if (totalBytes > 0)
            {
                var percent = (int)(5 + (totalRead * 85 / totalBytes));
                progress?.Report(Math.Min(percent, 90));
            }
        }

        progress?.Report(95);
        WriteLog($"Downloaded update to: {downloadPath}");
        return downloadPath;
    }

    /// <summary>
    /// Launches the external Update.exe with the zip path and current app exe,
    /// then shuts down the main application so the updater can replace files.
    /// </summary>
    public static bool LaunchExternalUpdater(string zipPath)
    {
        // Find Update.exe next to the main app
        var updaterPath = Path.Combine(AppInstallDir, "Update.exe");
        if (!File.Exists(updaterPath))
        {
            WriteLog($"Update.exe not found at: {updaterPath}");
            return false;
        }

        var appExe = Environment.ProcessPath ?? Path.Combine(AppInstallDir, "HISAB KITAB.exe");
        var pid = Environment.ProcessId;

        WriteLog($"Launching external updater: {updaterPath}");
        WriteLog($"  --zip \"{zipPath}\" --app \"{appExe}\" --pid {pid}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--zip \"{zipPath}\" --app \"{appExe}\" --pid {pid}",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"Failed to launch updater: {ex.Message}");
            return false;
        }
    }
}

public class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; set; }
    public string? LatestVersion { get; set; }
    public string? DownloadUrl { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? ErrorMessage { get; set; }
}

public class UpdateResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public bool RequiresRestart { get; set; }
}
