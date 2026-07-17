using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal static partial class SigningKeyStore
{
    private const string ExpectedPublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyJC7f5wQ5REEWdHzKuqXQVU4NjY8t17V3IHj9Ahd597HRhY6HZBxnzso1mIp0fzB8ZWu/Xgnvi2scepKCFnscVKoLaLSEQpanWtDHdA4sMCfveNJ9W/Tj54lgbt89mGaGNcteqr7L0elBSSzPyJxRLKUMbWD29D5fqkpa/tMFevwVfDAzBY2w9qbQL1cj2Y1in86q91oZOUYhaEFns4c6pYJ7Tm/G8pP8nQYXaP7El/m9hPFM3XIXGAh7O01+7ottIpacGfSOGkwa7Nufv+IbQnc1RKtqKg3/U3XLPllyfQNZyJ8n3RoVjwaXtTDPs1AACGFLnCuB2HSocNarphK5xKk5E5oeF/YvOI0EGYXzPl5Hs/ExvjJuJm1bhxFRBcIWFEAba7hH+JrPv6RIpEFHr/xWbqZagbRjSr5zRi8GkcG5KDJdOER6NP8ErNaIhOEiyPuPeW9VXzn4ch5s+BOxtyzGvYiXiht5yytpcXvEvK8t9L0issM5fuXRCD2/v/pAgMBAAE=";
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
        Save(key);
    }

    public static void ExportEncryptedBackup(string destinationPath, string passphrase)
    {
        if (passphrase.Length < 12)
            throw new InvalidOperationException("Use a backup password with at least 12 characters.");

        var privateKey = Encoding.UTF8.GetBytes(Load());
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 310_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[privateKey.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(derivedKey, tag.Length);
            aes.Encrypt(nonce, privateKey, cipher, tag);
            var backup = new SigningKeyBackup
            {
                Version = 1,
                Iterations = 310_000,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(cipher),
                Tag = Convert.ToBase64String(tag)
            };
            File.WriteAllText(destinationPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public static void ImportEncryptedBackup(string sourcePath, string passphrase)
    {
        try
        {
            var backup = JsonSerializer.Deserialize<SigningKeyBackup>(File.ReadAllText(sourcePath))
                ?? throw new InvalidOperationException("The signing-key backup is empty.");
            if (backup.Version != 1 || backup.Iterations < 100_000)
                throw new InvalidOperationException("The signing-key backup format is not supported.");

            var salt = Convert.FromBase64String(backup.Salt);
            var nonce = Convert.FromBase64String(backup.Nonce);
            var cipher = Convert.FromBase64String(backup.Ciphertext);
            var tag = Convert.FromBase64String(backup.Tag);
            var derivedKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, backup.Iterations, HashAlgorithmName.SHA256, 32);
            var clear = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(derivedKey, tag.Length);
                aes.Decrypt(nonce, cipher, tag, clear);
                Save(Encoding.UTF8.GetString(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
                CryptographicOperations.ZeroMemory(derivedKey);
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The backup password is incorrect or the backup file was modified.", ex);
        }
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

    public static bool VerifyConfiguration()
    {
        var testData = Encoding.UTF8.GetBytes("HISAB KITAB WORKS SIGNING SELF TEST V2");
        using var signer = RSA.Create();
        signer.ImportRSAPrivateKey(Convert.FromBase64String(Load()), out _);
        var signature = signer.SignData(testData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ExpectedPublicKeyBase64), out _);
        return verifier.VerifyData(testData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static bool VerifyEncryptedBackupRoundTrip()
    {
        var original = Encoding.UTF8.GetBytes(Load());
        var backupPath = Path.Combine(Path.GetTempPath(), $"hisab-kitab-signing-test-{Guid.NewGuid():N}.hbsigningbackup");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        try
        {
            ExportEncryptedBackup(backupPath, password);
            ImportEncryptedBackup(backupPath, password);
            var restored = Encoding.UTF8.GetBytes(Load());
            try
            {
                return CryptographicOperations.FixedTimeEquals(original, restored);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(restored);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    private static void Save(string key)
    {
        Validate(key);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var clear = Encoding.UTF8.GetBytes(key);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeyPath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
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

    private sealed class SigningKeyBackup
    {
        public int Version { get; set; }
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
    }
}
