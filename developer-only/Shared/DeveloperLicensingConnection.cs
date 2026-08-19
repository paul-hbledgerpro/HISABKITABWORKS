using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HisabKitabWorks.DeveloperTools;

internal sealed record DeveloperLicensingConnectionProfile(
    string Server,
    string Username,
    string Password);

internal static class DeveloperLicensingConnection
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-DEVELOPER-LICENSING-V1");

    private static string ProfilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hisab Kitab",
            "DeveloperTools",
            "licensing-connection.bin");

    public static DeveloperLicensingConnectionProfile Load(string defaultServer)
    {
        try
        {
            if (!File.Exists(ProfilePath))
                return new(defaultServer, string.Empty, string.Empty);

            var encrypted = File.ReadAllBytes(ProfilePath);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<DeveloperLicensingConnectionProfile>(json)
                   ?? new(defaultServer, string.Empty, string.Empty);
        }
        catch
        {
            return new(defaultServer, string.Empty, string.Empty);
        }
    }

    public static void Save(string server, string username, string password)
    {
        var profile = new DeveloperLicensingConnectionProfile(
            server.Trim(),
            username.Trim(),
            password);
        var json = JsonSerializer.SerializeToUtf8Bytes(profile);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllBytes(ProfilePath, encrypted);
    }
}
