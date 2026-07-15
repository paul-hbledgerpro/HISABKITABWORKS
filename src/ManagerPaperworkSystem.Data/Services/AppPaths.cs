using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.Data.Services;

public sealed class AppPaths : IAppPaths
{
    public string AppDataDirectory { get; }
    public string DatabasePath { get; }
    public string BackupsDirectory { get; }

    // IMPORTANT: user requested data to be stored under this AppData folder name.
    private const string AppFolderName = "Hisab Kitab";

    public AppPaths()
    {
        // Per-user so no admin rights needed
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var newDir = Path.Combine(baseDir, AppFolderName);
        var legacyDirs = new[]
        {
            Path.Combine(baseDir, "Hisab Works"),
            Path.Combine(baseDir, "HB STORE LEDGER PRO"),
            Path.Combine(baseDir, "HB Store Ledger Pro"),
            Path.Combine(baseDir, "Manager Paperwork System")
        };

        Directory.CreateDirectory(newDir);

        // Migrate legacy data once (if present) into the new folder.
        try
        {
            foreach (var legacyDir in legacyDirs.Where(Directory.Exists))
            {
                var legacyDb1 = Path.Combine(legacyDir, "hb_storeledger.db");
                var legacyDb2 = Path.Combine(legacyDir, "managerpaperwork_v2.db");

                var targetDb = Path.Combine(newDir, "hb_storeledger.db");
                if (!File.Exists(targetDb))
                {
                    if (File.Exists(legacyDb1))
                        File.Copy(legacyDb1, targetDb, overwrite: false);
                    else if (File.Exists(legacyDb2))
                        File.Copy(legacyDb2, targetDb, overwrite: false);
                }

                // Copy backups folder if it exists and target is empty.
                var legacyBackups = Path.Combine(legacyDir, "Backups");
                var targetBackups = Path.Combine(newDir, "Backups");
                if (Directory.Exists(legacyBackups) && !Directory.Exists(targetBackups))
                {
                    DirectoryCopy(legacyBackups, targetBackups, copySubDirs: true);
                }

                CopyIfMissing(legacyDir, newDir, "connection_settings.json");
                CopyIfMissing(legacyDir, newDir, "license.json");
                CopyIfMissing(legacyDir, newDir, "pending_store_info.json");
            }
        }
        catch
        {
            // Migration failures should never block app startup.
        }

        AppDataDirectory = newDir;

        DatabasePath = Path.Combine(AppDataDirectory, "hb_storeledger.db");
        BackupsDirectory = Path.Combine(AppDataDirectory, "Backups");
        Directory.CreateDirectory(BackupsDirectory);
    }

    private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
    {
        var dir = new DirectoryInfo(sourceDirName);
        if (!dir.Exists) return;

        Directory.CreateDirectory(destDirName);

        foreach (var file in dir.GetFiles())
        {
            var tempPath = Path.Combine(destDirName, file.Name);
            if (!File.Exists(tempPath))
                file.CopyTo(tempPath, overwrite: false);
        }

        if (!copySubDirs) return;

        foreach (var subdir in dir.GetDirectories())
        {
            var tempPath = Path.Combine(destDirName, subdir.Name);
            DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
        }
    }

    private static void CopyIfMissing(string sourceDir, string destDir, string fileName)
    {
        var source = Path.Combine(sourceDir, fileName);
        var dest = Path.Combine(destDir, fileName);
        if (File.Exists(source) && !File.Exists(dest))
            File.Copy(source, dest, overwrite: false);
    }
}
