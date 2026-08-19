using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ManagerPaperworkSystem.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();

            // Clean up any leftover .old files from previous self-update
            try
            {
                foreach (var oldName in new[] { "Upgrade.exe.old", "Update.exe.old" })
                {
                    var oldFile = Path.Combine(AppContext.BaseDirectory, oldName);
                    if (File.Exists(oldFile)) File.Delete(oldFile);
                }
            }
            catch { }

            // Mode 1: Silent update (called from main app)
            //   --zip "C:\temp\update.zip" --app "C:\...\HISAB KITAB.exe" --pid 12345
            string? zipPath = GetArg(args, "--zip");
            string? appExe = GetArg(args, "--app");
            string? pidStr = GetArg(args, "--pid");
            string? downloadUrl = GetArg(args, "--download-url");
            string? targetVersion = GetArg(args, "--version");

            if (!string.IsNullOrWhiteSpace(zipPath) && !string.IsNullOrWhiteSpace(appExe))
            {
                RunSilentUpdate(zipPath, appExe, pidStr);
                return;
            }

            // Mode 2: Interactive GUI (launched directly by user)
            // Auto-detect the main app exe next to this updater
            if (string.IsNullOrWhiteSpace(appExe))
            {
                var dir = AppContext.BaseDirectory;
                var candidate = Path.Combine(dir, "HISAB KITAB.exe");
                if (File.Exists(candidate)) appExe = candidate;
            }

            Application.Run(new UpdateManagerForm(
                appExe,
                downloadUrl,
                targetVersion,
                pidStr));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Upgrade.exe failed to start:\n\n{ex}", "HISAB KITAB Upgrade", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SILENT UPDATE MODE (called from main app)
    // ═══════════════════════════════════════════════════════════════
    private static void RunSilentUpdate(string zipPath, string appExe, string? pidStr)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hisab Kitab", "Logs", "update_log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            void Log(string msg)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Updater] {msg}\n"); }
                catch { }
            }

            Log($"Silent update started. Zip: {zipPath}, App: {appExe}");

            // Wait for main app to close
            if (!string.IsNullOrWhiteSpace(pidStr) && int.TryParse(pidStr, out var pid))
            {
                Log($"Waiting for process {pid} to exit...");
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (!proc.WaitForExit(3000))
                    {
                        Log($"Process {pid} did not close promptly. Forcing it to exit.");
                        proc.Kill(true);
                        proc.WaitForExit(5000);
                    }
                }
                catch (ArgumentException) { Log("Process already exited."); }
            }

            // Kill by name as safety net
            var appName = Path.GetFileNameWithoutExtension(appExe);
            foreach (var p in Process.GetProcessesByName(appName))
            {
                try { p.CloseMainWindow(); if (!p.WaitForExit(1500)) p.Kill(true); } catch { }
            }
            Thread.Sleep(500);

            // Apply the update in place when the current account can write there.
            // Older Program Files installations are migrated to a per-user app
            // directory so standard/local Windows users can update without UAC.
            var destination = ResolveUpdateDestination(appExe, Log);
            ApplyZipUpdate(zipPath, destination.InstallDirectory, Log);
            if (destination.Migrated)
                CreateUserShortcuts(destination.AppExecutablePath, Log);

            // Relaunch
            Log("Relaunching application...");
            Thread.Sleep(500);
            Process.Start(new ProcessStartInfo
            {
                FileName = destination.AppExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = destination.InstallDirectory
            });
            Log("Update complete.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            try { if (File.Exists(appExe)) Process.Start(new ProcessStartInfo { FileName = appExe, UseShellExecute = true }); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SHARED: Apply zip to install directory
    // ═══════════════════════════════════════════════════════════════
    internal static void ApplyZipUpdate(string zipPath, string installDir, Action<string>? log = null)
    {
        void Log(string msg) => log?.Invoke(msg);

        // Backup
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hisab Kitab", "update_backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupDir);

        // Extract
        var tempDir = Path.Combine(Path.GetTempPath(), "HBUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Log($"Extracting to {tempDir}...");
        ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

        var files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
        Log($"Found {files.Length} files in update package");

        int copied = 0, failed = 0;
        var failedFiles = new System.Collections.Generic.List<string>();

        foreach (var src in files)
        {
            var rel = Path.GetRelativePath(tempDir, src);
            var dest = Path.Combine(installDir, rel);
            try
            {
                // If the destination file is locked (Upgrade.exe replacing itself),
                // rename the old file first, then copy the new one
                if (File.Exists(dest))
                {
                    var backupDest = Path.Combine(backupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupDest)!);
                    File.Copy(dest, backupDest, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                try
                {
                    CopyWithRetry(src, dest);
                }
                catch (IOException) when (rel.Equals("Upgrade.exe", StringComparison.OrdinalIgnoreCase) || rel.Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // The updater is locked because it is currently running.
                    // Rename old one to .old, copy new one, schedule cleanup.
                    var oldPath = dest + ".old";
                    try { File.Delete(oldPath); } catch { }
                    File.Move(dest, oldPath);
                    CopyWithRetry(src, dest);
                    Log($"{rel} self-updated via rename trick");
                }

                copied++;
            }
            catch (Exception ex) { failedFiles.Add($"{rel}: {ex.Message}"); failed++; }
        }

        Log($"Copied {copied} files, {failed} failed");
        if (failed > 0) { foreach (var f in failedFiles) Log($"  FAILED: {f}"); }

        try { Directory.Delete(tempDir, true); } catch { }
        try { File.Delete(zipPath); } catch { }

        if (failed > 0)
        {
            throw new IOException(
                $"The update could not replace {failed} file(s).\n\n" +
                string.Join("\n", failedFiles.Take(5)));
        }
    }

    internal static UpdateDestination ResolveUpdateDestination(
        string appExe,
        Action<string>? log = null)
    {
        var currentDirectory = Path.GetDirectoryName(appExe)
            ?? throw new InvalidOperationException("The HISAB KITAB installation directory could not be determined.");
        if (CanWriteToDirectory(currentDirectory))
            return new UpdateDestination(currentDirectory, appExe, Migrated: false);

        var applicationName = GetApplicationName(appExe);
        var perUserDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            applicationName);
        Directory.CreateDirectory(perUserDirectory);
        if (!CanWriteToDirectory(perUserDirectory))
            throw new UnauthorizedAccessException(
                "The current Windows account cannot write to the existing installation or its per-user application folder.");

        var perUserAppExe = Path.Combine(perUserDirectory, Path.GetFileName(appExe));
        log?.Invoke(
            $"The existing installation is not writable by this Windows account. " +
            $"Migrating the update to {perUserDirectory}.");
        return new UpdateDestination(perUserDirectory, perUserAppExe, Migrated: true);
    }

    internal static void CreateUserShortcuts(string appExe, Action<string>? log = null)
    {
        try
        {
            var applicationName = GetApplicationName(appExe);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support could not be started.");
            try
            {
                var shortcutPaths = new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        $"{applicationName}.lnk"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                        applicationName,
                        $"{applicationName}.lnk")
                };

                foreach (var shortcutPath in shortcutPaths)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    try
                    {
                        shortcut.TargetPath = appExe;
                        shortcut.WorkingDirectory = Path.GetDirectoryName(appExe)!;
                        shortcut.IconLocation = $"{appExe},0";
                        shortcut.Description = applicationName;
                        shortcut.Save();
                    }
                    finally
                    {
                        if (Marshal.IsComObject(shortcut))
                            Marshal.FinalReleaseComObject(shortcut);
                    }
                }
            }
            finally
            {
                if (Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }

            log?.Invoke("Updated the current user's HISAB KITAB shortcuts.");
        }
        catch (Exception exception)
        {
            // The update itself is still usable and is relaunched immediately.
            log?.Invoke($"Could not update user shortcuts: {exception.Message}");
        }
    }

    private static string GetApplicationName(string appExe)
    {
        var executableName = Path.GetFileNameWithoutExtension(appExe).Trim();
        return executableName.Equals("HISAB KITAB", StringComparison.OrdinalIgnoreCase)
            ? "HISAB KITAB WORKS"
            : executableName;
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(
                directory,
                $".hisab-kitab-update-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            using (new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CopyWithRetry(string source, string destination)
    {
        const int maximumAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(250 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

internal sealed record UpdateDestination(
    string InstallDirectory,
    string AppExecutablePath,
    bool Migrated);
