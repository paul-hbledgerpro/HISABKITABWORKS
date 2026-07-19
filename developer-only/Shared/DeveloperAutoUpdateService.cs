using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace HisabKitabWorks.DeveloperUpdates;

internal static class DeveloperAutoUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/paul-hbledgerpro/HISABKITABWORKS/releases/latest";

    public static bool InstallLatestIfAvailable(string applicationName, string assetPrefix)
    {
        try
        {
            var update = FindUpdateAsync(assetPrefix).GetAwaiter().GetResult();
            if (update is null)
                return false;

            var updaterDirectory = Path.Combine(AppContext.BaseDirectory, "UpdaterPayload");
            var installedUpdater = Path.Combine(updaterDirectory, "Upgrade.exe");
            if (!File.Exists(installedUpdater))
            {
                MessageBox.Show(
                    $"{applicationName} {update.Value.Version} is available, but this installation " +
                    "does not yet contain the automatic updater.\n\nInstall the latest setup package once; " +
                    "future releases will then update automatically.",
                    "Software Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }

            var workingDirectory = PrepareUpdaterWorkingCopy(updaterDirectory, applicationName);
            var updaterPath = Path.Combine(workingDirectory, "Upgrade.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };
            startInfo.ArgumentList.Add("--download-url");
            startInfo.ArgumentList.Add(update.Value.DownloadUrl);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(update.Value.Version);
            startInfo.ArgumentList.Add("--app");
            startInfo.ArgumentList.Add(Application.ExecutablePath);
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--required");
            startInfo.ArgumentList.Add("true");

            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The automatic updater could not be started.");
            return true;
        }
        catch (HttpRequestException)
        {
            // Developer tools remain usable when GitHub or the internet is unavailable.
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The automatic update check could not be completed.\n\n{ex.Message}",
                "Software Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }

    private static async Task<(string Version, string DownloadUrl)?> FindUpdateAsync(
        string assetPrefix)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HisabKitabWorksDeveloperTool", CurrentVersion()));
        using var response = await client.GetAsync(LatestReleaseApi);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var release = await JsonDocument.ParseAsync(stream);
        var root = release.RootElement;
        var version = NormalizeVersion(root.GetProperty("tag_name").GetString());
        if (!Version.TryParse(version, out var latest) ||
            !Version.TryParse(CurrentVersion(), out var current) ||
            latest <= current)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets))
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(url))
            {
                return (version, url);
            }
        }

        return null;
    }

    private static string PrepareUpdaterWorkingCopy(string sourceDirectory, string applicationName)
    {
        var safeName = string.Concat(applicationName.Select(ch =>
            char.IsLetterOrDigit(ch) ? ch : '_'));
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "HISAB_KITAB_UPDATER",
            safeName,
            $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        foreach (var source in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            var destination = Path.Combine(workingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }

        if (!File.Exists(Path.Combine(workingDirectory, "Upgrade.exe")))
            throw new InvalidOperationException("The updater runtime could not be prepared.");
        return workingDirectory;
    }

    private static string CurrentVersion()
    {
        try
        {
            var versionFile = Path.Combine(AppContext.BaseDirectory, "version.txt");
            if (File.Exists(versionFile))
            {
                var installedVersion = NormalizeVersion(File.ReadAllText(versionFile));
                if (Version.TryParse(installedVersion, out _))
                    return installedVersion;
            }
        }
        catch
        {
            // Fall back to the executable metadata when the version file is unavailable.
        }

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static string NormalizeVersion(string? value)
        => (value ?? "0.0.0").Trim().TrimStart('v', 'V').Split('-', '+')[0];
}
