using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal static partial class SigningKeyStore
{
    private const string ExpectedPublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA3akO1aSKySRsUf4XU9E/EcopIZEznVNKKHEiWSdE9BWfqDyrYtFrIPFjHC9TnHmECp5NDsn40vuR/ZVFgtohGw8P0b9ZceB84nfFeyCnAQvg1OiSHUuT8UK9CYffHssu7heBRHO/FJCXJZ4otqfenI9C6y3Jzr/wBhfQ9PHp4YraccWtoXc/U6PH7MbqWjc6p49ubdamn6lShxsbHv8X1sdf2lMDY4odBajn4rqrKV2VFdMC6T9rmLApPVlhzJ+6kgrHMe3ciwOF8EbfbvE7gq82ltPwWTviDBCxDK6OCjsx7jCt84TDvprv8I1c+8P1Qy9s2Orw0i3p4XXgWjsR7FWGf6L5x+xj8qxCUOyxP3/3NyFQBQTX+FTICCDZKafscWGbqT6OJV7skEaSBkThOMJuM+iMzSbQDb82CqjM3MIQFnsXgsP4aAjhCAFELVDj9u+tX1x1UJNxHZdB2eK1R2aLzrNVQQWaTSY2AulRmTEUH9XlK5BUf/5zpOVBDfURAgMBAAE=";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-LICENSE-GENERATOR-V1");
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HISAB KITAB WORKS",
        "License Generator",
        "signing-key.dat");

    public static bool IsConfigured => File.Exists(KeyPath);

    public static void Import(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath).Trim();
        var key = ExtractBase64Key(source);
        Validate(key);

        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, protectedBytes);
    }

    public static string Load()
    {
        if (!File.Exists(KeyPath))
            throw new InvalidOperationException("The offline-license signing key is not configured on this Windows admin account.");

        var protectedBytes = File.ReadAllBytes(KeyPath);
        var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var key = Encoding.UTF8.GetString(clearBytes);
        Validate(key);
        return key;
    }

    private static string ExtractBase64Key(string source)
    {
        if (source.Contains("BEGIN RSA PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return source
                .Replace("-----BEGIN RSA PRIVATE KEY-----", "", StringComparison.OrdinalIgnoreCase)
                .Replace("-----END RSA PRIVATE KEY-----", "", StringComparison.OrdinalIgnoreCase)
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();
        }

        var codeMatch = EmbeddedKeyRegex().Match(source);
        if (codeMatch.Success)
            return codeMatch.Groups[1].Value;

        return Regex.Replace(source, @"\s+", "");
    }

    private static void Validate(string key)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(Convert.FromBase64String(key), out _);
            using var expected = RSA.Create();
            expected.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ExpectedPublicKeyBase64), out _);
            if (!CryptographicOperations.FixedTimeEquals(rsa.ExportSubjectPublicKeyInfo(), expected.ExportSubjectPublicKeyInfo()))
                throw new InvalidOperationException("The private signing key does not match the public key trusted by HISAB KITAB.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The selected file does not contain a valid RSA private signing key.", ex);
        }
    }

    [GeneratedRegex("OfflineLicensePrivateKeyBase64\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedKeyRegex();
}
