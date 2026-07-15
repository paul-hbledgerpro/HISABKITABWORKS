using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ManagerPaperworkSystem.Updater;

public class UpdateForm : Form
{
    private readonly TextBox _txtInstaller = new() { ReadOnly = true, Dock = DockStyle.Top };
    private readonly Button _btnBrowse = new() { Text = "Browse update (Setup.exe or Update Package)...", Dock = DockStyle.Top, Height = 36 };
    private readonly Button _btnUpdate = new() { Text = "Update Now", Dock = DockStyle.Top, Height = 44 };
    private readonly Label _lbl = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };

    private readonly string? _appExe;

    public UpdateForm(string? installerPath, string? appExe)
    {
        Text = "HISAB KITAB - Update";
        Width = 560;
        Height = 220;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        _appExe = appExe;

        _lbl.Text = "Select an update file and click Update.\r\n\r\n" +
                    "• Recommended: Setup.exe (standard upgrade)\r\n" +
                    "• Or: Update Package (*.mpsupdate / *.zip)\r\n\r\n" +
                    "The updater will close HISAB KITAB, apply the update, and restart it.\r\n";

        _btnBrowse.Click += (_, _) => Browse();
        _btnUpdate.Click += (_, _) => RunUpdate();

        Controls.Add(_lbl);
        Controls.Add(_btnUpdate);
        Controls.Add(_btnBrowse);
        Controls.Add(_txtInstaller);

        if (!string.IsNullOrWhiteSpace(installerPath))
        {
            _txtInstaller.Text = installerPath;
        }
        else
        {
            // Auto-detect a Setup.exe next to Update.exe (most common "professional software" pattern)
            try
            {
                var here = AppContext.BaseDirectory;
                var candidate = Directory.GetFiles(here, "*setup*.exe")
                    .Concat(Directory.GetFiles(here, "Setup.exe"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (candidate is not null)
                    _txtInstaller.Text = candidate.FullName;
            }
            catch { }
        }
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Setup.exe",
            Filter = "Update Files|*.exe;*.mpsupdate;*.zip|Installer (Setup.exe)|*.exe|Update Package (*.mpsupdate;*.zip)|*.mpsupdate;*.zip",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtInstaller.Text = dlg.FileName;
    }

    private void RunUpdate()
    {
        var installer = _txtInstaller.Text?.Trim();
        if (string.IsNullOrWhiteSpace(installer) || !File.Exists(installer))
        {
            MessageBox.Show(this, "Please select a valid update file (Setup.exe or update package).", "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            CloseRunningApp();

            var ext = Path.GetExtension(installer).ToLowerInvariant();
            if (ext == ".mpsupdate" || ext == ".zip")
            {
                ApplyPackageUpdate(installer);
            }
            else
            {
                RunInstaller(installer);
            }

            // Restart app if we know where it is.
            if (!string.IsNullOrWhiteSpace(_appExe) && File.Exists(_appExe))
            {
                Process.Start(new ProcessStartInfo { FileName = _appExe, UseShellExecute = true });
            }

            MessageBox.Show(this, "Updated successfully.", "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Update Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunInstaller(string installer)
    {
        // Run installer interactively (most reliable). You can switch to /SILENT later if you want.
        var psi = new ProcessStartInfo
        {
            FileName = installer,
            UseShellExecute = true
        };

        var p = Process.Start(psi);
        if (p is null)
            throw new InvalidOperationException("Unable to start installer.");

        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Installer finished with exit code {p.ExitCode}.");
    }

    private void CloseRunningApp()
    {
        if (string.IsNullOrWhiteSpace(_appExe)) return;
        try
        {
            var name = Path.GetFileNameWithoutExtension(_appExe);
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000))
                        p.Kill(true);
                }
                catch { }
            }
        }
        catch { }
    }

    private void ApplyPackageUpdate(string packagePath)
    {
        // A simple "drop-in" update package that contains the app's published files.
        // Structure: package root contains files to copy into the install folder.
        var installDir = AppContext.BaseDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "ManagerPaperworkSystemUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, tempDir, overwriteFiles: true);

            // Copy everything from temp into install folder
            foreach (var src in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(tempDir, src);
                var dest = Path.Combine(installDir, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
