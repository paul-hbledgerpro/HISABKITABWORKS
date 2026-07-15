using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

            Application.Run(new UpdateManagerForm(appExe));
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
            var installDir = Path.GetDirectoryName(appExe)!;
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
                    if (!proc.WaitForExit(15000))
                    {
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
                try { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(true); } catch { }
            }
            Thread.Sleep(1500);

            // Apply update
            ApplyZipUpdate(zipPath, installDir, Log);

            // Relaunch
            Log("Relaunching application...");
            Thread.Sleep(500);
            Process.Start(new ProcessStartInfo { FileName = appExe, UseShellExecute = true, WorkingDirectory = installDir });
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
                    File.Copy(src, dest, overwrite: true);
                }
                catch (IOException) when (rel.Equals("Upgrade.exe", StringComparison.OrdinalIgnoreCase) || rel.Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // The updater is locked because it is currently running.
                    // Rename old one to .old, copy new one, schedule cleanup.
                    var oldPath = dest + ".old";
                    try { File.Delete(oldPath); } catch { }
                    File.Move(dest, oldPath);
                    File.Copy(src, dest, overwrite: true);
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
            MessageBox.Show($"Update partially applied: {copied} updated, {failed} failed.\n\n" +
                string.Join("\n", failedFiles.Take(5)), "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
