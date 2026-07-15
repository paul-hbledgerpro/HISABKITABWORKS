using System;
using System.Windows;
using ManagerPaperworkSystem.UI.Services;

namespace ManagerPaperworkSystem.UI.Views;

public partial class UpdateWindow : Window
{
    private UpdateCheckResult? _updateResult;

    public UpdateWindow()
    {
        InitializeComponent();
        txtCurrentVersion.Text = UpdateService.CurrentVersion;
    }

    // ═══════════════════════════════════════════════════════════════
    // VERIFY PASSWORD
    // ═══════════════════════════════════════════════════════════════
    private void BtnVerify_Click(object sender, RoutedEventArgs e)
    {
        txtPasswordError.Text = "";

        var password = txtPassword.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            txtPasswordError.Text = "Please enter the master password.";
            return;
        }

        if (!UpdateService.VerifyMasterPassword(password))
        {
            txtPasswordError.Text = "Invalid master password. Access denied.";
            txtPassword.Clear();
            return;
        }

        // Password verified — show update options
        pnlPassword.Visibility = Visibility.Collapsed;
        pnlUpdateOptions.Visibility = Visibility.Visible;
        pnlRollback.Visibility = Visibility.Visible;
        txtStatus.Text = "Select an update file or check the server for updates.";
    }

    // ═══════════════════════════════════════════════════════════════
    // BROWSE FOR LOCAL UPDATE FILE
    // ═══════════════════════════════════════════════════════════════
    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Update Package",
            Filter = "Update Files (*.zip;*.mpsupdate)|*.zip;*.mpsupdate|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) == true)
        {
            txtFilePath.Text = dlg.FileName;
            btnApplyLocal.IsEnabled = true;
            txtStatus.Text = $"Selected: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // APPLY LOCAL UPDATE
    // ═══════════════════════════════════════════════════════════════
    private async void BtnApplyLocal_Click(object sender, RoutedEventArgs e)
    {
        var filePath = txtFilePath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            txtStatus.Text = "Please select a valid update file first.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Apply update from:\n{System.IO.Path.GetFileName(filePath)}\n\nThe app will close, update, and restart. Continue?",
            "Confirm Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        // Try external updater first
        if (UpdateService.LaunchExternalUpdater(filePath))
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        // Fallback: in-process update (can't replace the running .exe)
        pnlUpdateOptions.Visibility = Visibility.Collapsed;
        pnlRollback.Visibility = Visibility.Collapsed;
        pnlProgress.Visibility = Visibility.Visible;
        btnClose.IsEnabled = false;
        txtProgressLabel.Text = "Applying update...";

        var progress = new Progress<int>(percent =>
        {
            progressBar.Value = percent;
            txtProgress.Text = $"{percent}%";
        });

        try
        {
            var result = await UpdateService.ApplyLocalUpdateAsync(filePath, progress);

            pnlProgress.Visibility = Visibility.Collapsed;
            btnClose.IsEnabled = true;

            if (result.Success)
            {
                txtStatus.Text = result.Message ?? "Update applied.";
                if (result.RequiresRestart)
                {
                    var restart = System.Windows.MessageBox.Show(
                        "Update applied successfully!\n\nRestart now?",
                        "Update Complete",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (restart == MessageBoxResult.Yes)
                    {
                        var exePath = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exePath))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            { FileName = exePath, UseShellExecute = true });
                        System.Windows.Application.Current.Shutdown();
                    }
                }
            }
            else
            {
                txtStatus.Text = result.Message ?? "Update failed.";
                pnlUpdateOptions.Visibility = Visibility.Visible;
                pnlRollback.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            pnlProgress.Visibility = Visibility.Collapsed;
            btnClose.IsEnabled = true;
            pnlUpdateOptions.Visibility = Visibility.Visible;
            pnlRollback.Visibility = Visibility.Visible;
            txtStatus.Text = $"Error: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CHECK SERVER FOR UPDATES
    // ═══════════════════════════════════════════════════════════════
    private async void BtnCheckServer_Click(object sender, RoutedEventArgs e)
    {
        btnCheckServer.IsEnabled = false;
        txtStatus.Text = "Checking for updates online...";
        pnlServerUpdate.Visibility = Visibility.Collapsed;

        try
        {
            _updateResult = await UpdateService.CheckForUpdatesAsync();

            if (!string.IsNullOrEmpty(_updateResult.ErrorMessage))
            {
                txtStatus.Text = _updateResult.ErrorMessage;
            }
            else if (_updateResult.IsUpdateAvailable)
            {
                pnlServerUpdate.Visibility = Visibility.Visible;
                txtNewVersion.Text = _updateResult.LatestVersion ?? "Unknown";
                txtReleaseNotes.Text = _updateResult.ReleaseNotes ?? "";
                txtStatus.Text = "A new update is available!";
            }
            else
            {
                txtStatus.Text = "\u2713 Your application is up to date.";
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            btnCheckServer.IsEnabled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // DOWNLOAD FROM SERVER
    // ═══════════════════════════════════════════════════════════════
    private async void BtnDownloadServer_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult == null || !_updateResult.IsUpdateAvailable) return;

        pnlUpdateOptions.Visibility = Visibility.Collapsed;
        pnlRollback.Visibility = Visibility.Collapsed;
        pnlProgress.Visibility = Visibility.Visible;
        btnClose.IsEnabled = false;
        txtProgressLabel.Text = "Downloading update...";

        var progress = new Progress<int>(percent =>
        {
            progressBar.Value = percent;
            txtProgress.Text = $"{percent}%";
        });

        try
        {
            // Step 1: Download zip to temp
            var zipPath = await UpdateService.DownloadToTempAsync(
                _updateResult.DownloadUrl ?? "", progress);

            if (string.IsNullOrEmpty(zipPath) || !System.IO.File.Exists(zipPath))
            {
                txtStatus.Text = "Download failed — no file received.";
                pnlProgress.Visibility = Visibility.Collapsed;
                btnClose.IsEnabled = true;
                pnlUpdateOptions.Visibility = Visibility.Visible;
                return;
            }

            txtProgressLabel.Text = "Launching updater...";
            progressBar.Value = 100;

            // Step 2: Launch external Update.exe
            if (UpdateService.LaunchExternalUpdater(zipPath))
            {
                // Step 3: Shut down main app so updater can replace files
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                // Fallback: try the old in-process update
                txtProgressLabel.Text = "External updater not found, applying in-process...";
                var result = await UpdateService.ApplyLocalUpdateAsync(zipPath, progress);
                pnlProgress.Visibility = Visibility.Collapsed;
                btnClose.IsEnabled = true;

                if (result.Success)
                {
                    txtStatus.Text = result.Message ?? "Update applied.";
                    if (result.RequiresRestart)
                    {
                        var restart = System.Windows.MessageBox.Show(
                            "Update applied! Restart now?", "Update Complete",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (restart == MessageBoxResult.Yes)
                        {
                            var exePath = Environment.ProcessPath;
                            if (!string.IsNullOrEmpty(exePath))
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                { FileName = exePath, UseShellExecute = true });
                            System.Windows.Application.Current.Shutdown();
                        }
                    }
                }
                else
                {
                    txtStatus.Text = result.Message ?? "Update failed.";
                    pnlUpdateOptions.Visibility = Visibility.Visible;
                    pnlRollback.Visibility = Visibility.Visible;
                }
            }
        }
        catch (Exception ex)
        {
            pnlProgress.Visibility = Visibility.Collapsed;
            btnClose.IsEnabled = true;
            pnlUpdateOptions.Visibility = Visibility.Visible;
            pnlRollback.Visibility = Visibility.Visible;
            txtStatus.Text = $"Download failed: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // ROLLBACK
    // ═══════════════════════════════════════════════════════════════
    private void BtnRollback_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "This will restore the previous version of the application.\n\nContinue?",
            "Confirm Rollback",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var result = UpdateService.RollbackLastUpdate();

        if (result.Success)
        {
            txtStatus.Text = result.Message ?? "Rollback complete.";

            if (result.RequiresRestart)
            {
                var restart = System.Windows.MessageBox.Show(
                    "Rollback complete. Restart now?",
                    "Rollback",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (restart == MessageBoxResult.Yes)
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true
                        });
                    System.Windows.Application.Current.Shutdown();
                }
            }
        }
        else
        {
            txtStatus.Text = result.Message ?? "Rollback failed.";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CLOSE
    // ═══════════════════════════════════════════════════════════════
    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
