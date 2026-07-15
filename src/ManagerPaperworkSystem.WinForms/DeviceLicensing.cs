using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ManagerPaperworkSystem.WinForms;

internal enum DeviceLicenseStatus
{
    Missing,
    Valid,
    Expired,
    Invalid
}

internal sealed record DeviceLicenseValidation(DeviceLicenseStatus Status, string Message, DeviceLicensePayloadV2? Payload = null);

internal static class LicenseRuntime
{
    public static bool IsReadOnly { get; set; }
    public static DeviceLicensePayloadV2? CurrentLicense { get; set; }
}

internal static class DeviceLicenseService
{
    // The matching private key remains only in the administrator License Generator.
    internal const string LicenseSigningPublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA41Pt4R+4COqv01HNi5KRVe+Ws0yQjhcaj19XgXO7kZiXjSYOqjaqPPrGDnW93Q/tk5boAic+YyxhaVtEJ4AF9BONKUGmamKKc3Y4M9vO/kZAr3n7t2/h3EVNVoJUWL4Xpe0FL8+Ehr3tbejVayBCZ5xsrrzdzXFRE2CTlP6dFQP9TFsQGzceZu7EIStttZ/VEZcmQQ++BSPgqv41qlfIulU9ufeDDYpi6s4KJQkZIUzcrxVhGdhfBvPE7yELQYn7pXlpvSZfeWuIbFoc1DxpGYmJlQktam6kDUgp/QnKe//V+N5eW0vJM40RnwhxAyiNylbB8ie++QlWgZlac2XL2lAHDrvUOJahsB7G06qTgu8yx17bH27o68V2YZiuLVNpY44ofB1VFn0aadK+rHxvMiQeZ4gC8fauP/5f28R+Iw1H/YM1oIwXOekkaZS+J0HtYje3Sddu+H0V8/tBA0yKHjNxPRiWrxTYdlNv0vFJ1WpLx1u8UTbQBoj7b2Nqg8aRAgMBAAE=";

    private const string IdentityFileName = "device-identity.json";
    private const string InstalledLicenseFileName = "device-license.hblicense";
    private const string ClockStateFileName = "device-license-state.dat";
    private const string ProtectedConnectionFileName = "connection_settings.protected";
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-DEVICE-LICENSE-V2");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string InstalledLicensePath => Path.Combine(AppBootstrap.AppDataPath, InstalledLicenseFileName);
    public static string ProtectedConnectionPath => Path.Combine(AppBootstrap.AppDataPath, ProtectedConnectionFileName);

    public static DeviceIdentityRecord GetOrCreateIdentity()
    {
        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        var path = Path.Combine(AppBootstrap.AppDataPath, IdentityFileName);
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<DeviceIdentityRecord>(File.ReadAllText(path), JsonOptions);
                if (existing is not null && !string.IsNullOrWhiteSpace(existing.KeyName))
                {
                    using var key = OpenKey(existing);
                    using var rsa = new RSACng(key);
                    var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
                    var deviceId = BuildDeviceId(Convert.FromBase64String(publicKey));
                    if (string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal) &&
                        string.Equals(existing.PublicKey, publicKey, StringComparison.Ordinal))
                        return existing;
                }
            }
            catch { }
        }

        var record = CreateIdentity();
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
        return record;
    }

    public static DeviceLicenseRequestV2 CreateRequest(string businessName, string subscriptionKey)
    {
        if (string.IsNullOrWhiteSpace(businessName))
            throw new InvalidOperationException("Enter the store or business name before exporting the request.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(subscriptionKey.Trim(), @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
            throw new InvalidOperationException("Enter the subscription key in the format HBL-XXXX-XXXX-XXXX.");

        var identity = GetOrCreateIdentity();
        var request = new DeviceLicenseRequestV2
        {
            Version = 2,
            RequestId = Guid.NewGuid().ToString("N"),
            BusinessName = businessName.Trim(),
            SubscriptionKey = subscriptionKey.Trim().ToUpperInvariant(),
            DeviceId = identity.DeviceId,
            InstallationId = identity.InstallationId,
            DeviceName = Environment.MachineName,
            DevicePublicKey = identity.PublicKey,
            FingerprintHash = BuildFingerprintHash(),
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };

        using var key = OpenKey(identity);
        using var rsa = new RSACng(key);
        request.Proof = Convert.ToBase64String(rsa.SignData(
            Encoding.UTF8.GetBytes(request.SigningText()),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss));
        return request;
    }

    public static void ExportRequest(string path, string businessName, string subscriptionKey)
    {
        var request = CreateRequest(businessName, subscriptionKey);
        File.WriteAllText(path, JsonSerializer.Serialize(request, JsonOptions));
    }

    public static DeviceLicenseValidation ValidateInstalledLicense(bool updateClockState = true)
    {
        if (!File.Exists(InstalledLicensePath))
            return new(DeviceLicenseStatus.Missing, "This PC does not have a version-2 device license.");

        try
        {
            var validation = ValidateLicenseFile(InstalledLicensePath, allowExpired: true, updateClockState);
            LicenseRuntime.CurrentLicense = validation.Payload;
            LicenseRuntime.IsReadOnly = validation.Status == DeviceLicenseStatus.Expired;
            return validation;
        }
        catch (Exception ex)
        {
            LicenseRuntime.CurrentLicense = null;
            LicenseRuntime.IsReadOnly = false;
            return new(DeviceLicenseStatus.Invalid, ex.Message);
        }
    }

    public static DeviceLicenseValidation InstallLicense(string sourcePath)
    {
        var validation = ValidateLicenseFile(sourcePath, allowExpired: false, updateClockState: false);
        if (validation.Status != DeviceLicenseStatus.Valid || validation.Payload is null)
            return validation;

        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        File.Copy(sourcePath, InstalledLicensePath, true);
        SaveProtectedConnection(validation.Payload);
        SaveLicenseSummary(validation.Payload);
        SaveClockState(validation.Payload, DateTime.UtcNow);
        LicenseRuntime.CurrentLicense = validation.Payload;
        LicenseRuntime.IsReadOnly = false;
        return validation;
    }

    public static DeviceLicenseValidation ValidateLicenseFile(string path, bool allowExpired, bool updateClockState)
    {
        var envelope = JsonSerializer.Deserialize<DeviceLicenseEnvelopeV2>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The selected license file is not valid.");
        if (envelope.Version != 2)
            throw new InvalidOperationException("This is not a version-2 device license. Export a new PC request and issue a device-bound license.");

        var payloadBytes = Convert.FromBase64String(envelope.Payload);
        var signatureBytes = Convert.FromBase64String(envelope.Signature);
        using (var signer = RSA.Create())
        {
            signer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicenseSigningPublicKeyBase64), out _);
            if (!signer.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException("The license signature is invalid. The file was not issued by the authorized License Generator.");
        }

        var payload = JsonSerializer.Deserialize<DeviceLicensePayloadV2>(payloadBytes, JsonOptions)
            ?? throw new InvalidOperationException("The license payload is missing.");
        var identity = GetOrCreateIdentity();
        if (!string.Equals(payload.DeviceId, identity.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(payload.DevicePublicKey, identity.PublicKey, StringComparison.Ordinal))
            throw new InvalidOperationException("This license belongs to a different PC.");

        VerifyPrivateKeyPossession(identity);
        if (!string.Equals(payload.Status, "Active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This device license has been revoked or disabled.");
        if (!DateTime.TryParse(payload.IssuedUtc, out var issuedUtc) ||
            !DateTime.TryParse(payload.ExpiresUtc, out var expiresUtc))
            throw new InvalidOperationException("The license dates are invalid.");

        var now = DateTime.UtcNow;
        issuedUtc = issuedUtc.ToUniversalTime();
        expiresUtc = expiresUtc.ToUniversalTime();
        if (issuedUtc > now.AddMinutes(10))
            throw new InvalidOperationException("The computer clock is earlier than the license issue time.");
        ValidateClockState(payload, now);

        if (expiresUtc < now)
        {
            if (!allowExpired)
                throw new InvalidOperationException($"This device license expired on {expiresUtc:MM/dd/yyyy}.");
            return new(DeviceLicenseStatus.Expired,
                $"The subscription for this PC expired on {expiresUtc:MM/dd/yyyy}. The application will open in read-only mode.", payload);
        }

        if (updateClockState)
            SaveClockState(payload, now);
        return new(DeviceLicenseStatus.Valid, $"Licensed to {payload.BusinessName} until {expiresUtc:MM/dd/yyyy}.", payload);
    }

    public static DatabaseConnectionSettings? LoadProtectedConnection()
    {
        if (!File.Exists(ProtectedConnectionPath))
            return null;
        var protectedBytes = File.ReadAllBytes(ProtectedConnectionPath);
        var clear = ProtectedData.Unprotect(protectedBytes, ProtectionEntropy, DataProtectionScope.LocalMachine);
        return JsonSerializer.Deserialize<DatabaseConnectionSettings>(clear, JsonOptions);
    }

    private static DeviceIdentityRecord CreateIdentity()
    {
        var installationId = Guid.NewGuid().ToString("N");
        var keyName = $"HisabKitabWorks-Device-{installationId}";
        var provider = TryCreateKey(keyName, "Microsoft Platform Crypto Provider")
            ? "Microsoft Platform Crypto Provider"
            : CreateSoftwareKey(keyName);

        var record = new DeviceIdentityRecord
        {
            InstallationId = installationId,
            KeyName = keyName,
            Provider = provider,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
        using var key = OpenKey(record);
        using var rsa = new RSACng(key);
        var publicBytes = rsa.ExportSubjectPublicKeyInfo();
        record.PublicKey = Convert.ToBase64String(publicBytes);
        record.DeviceId = BuildDeviceId(publicBytes);
        return record;
    }

    private static bool TryCreateKey(string keyName, string providerName)
    {
        try
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = new CngProvider(providerName),
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            using var key = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateSoftwareKey(string keyName)
    {
        const string provider = "Microsoft Software Key Storage Provider";
        if (!TryCreateKey(keyName, provider))
            throw new InvalidOperationException("Windows could not create the protected identity key for this PC.");
        return provider;
    }

    private static CngKey OpenKey(DeviceIdentityRecord identity)
        => CngKey.Open(identity.KeyName, new CngProvider(identity.Provider));

    private static void VerifyPrivateKeyPossession(DeviceIdentityRecord identity)
    {
        var challenge = RandomNumberGenerator.GetBytes(48);
        using var key = OpenKey(identity);
        using var privateRsa = new RSACng(key);
        var signature = privateRsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        using var publicRsa = RSA.Create();
        publicRsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey), out _);
        if (!publicRsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidOperationException("This PC cannot prove ownership of its protected device key.");
    }

    private static string BuildDeviceId(byte[] publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(publicKey));
        return $"HKD-{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    private static string BuildFingerprintHash()
    {
        var machineGuid = "";
        try
        {
            machineGuid = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")
                ?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { }
        var source = $"{machineGuid}|{Environment.MachineName}|{Environment.OSVersion.VersionString}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static void SaveProtectedConnection(DeviceLicensePayloadV2 payload)
    {
        var identity = GetOrCreateIdentity();
        using var key = OpenKey(identity);
        using var rsa = new RSACng(key);
        var aesKey = rsa.Decrypt(Convert.FromBase64String(payload.EncryptedConnectionKey), RSAEncryptionPadding.OaepSHA256);
        var cipher = Convert.FromBase64String(payload.EncryptedConnection);
        var nonce = Convert.FromBase64String(payload.ConnectionNonce);
        var tag = Convert.FromBase64String(payload.ConnectionTag);
        var clear = new byte[cipher.Length];
        using (var aes = new AesGcm(aesKey, tag.Length))
            aes.Decrypt(nonce, cipher, tag, clear);

        // Validate before protecting the connection for this Windows computer.
        var settings = JsonSerializer.Deserialize<DatabaseConnectionSettings>(clear, JsonOptions)
            ?? throw new InvalidOperationException("The encrypted database connection is invalid.");
        if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Database))
            throw new InvalidOperationException("The device license does not contain a usable database connection.");

        var protectedBytes = ProtectedData.Protect(clear, ProtectionEntropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(ProtectedConnectionPath, protectedBytes);
        CryptographicOperations.ZeroMemory(clear);
        CryptographicOperations.ZeroMemory(aesKey);
    }

    private static void SaveLicenseSummary(DeviceLicensePayloadV2 payload)
    {
        File.WriteAllText(AppBootstrap.LicenseFilePath, JsonSerializer.Serialize(new
        {
            Version = 2,
            payload.LicenseKey,
            payload.BusinessName,
            payload.DeviceId,
            payload.DeviceName,
            payload.MaxDevices,
            payload.MaxStores,
            payload.MaxUsers,
            ExpiresDate = payload.ExpiresUtc,
            ActivatedAt = DateTime.UtcNow.ToString("O"),
            ActivationMode = "DeviceBoundOffline"
        }, JsonOptions));
    }

    private static void ValidateClockState(DeviceLicensePayloadV2 payload, DateTime nowUtc)
    {
        var statePath = Path.Combine(AppBootstrap.AppDataPath, ClockStateFileName);
        if (!File.Exists(statePath))
            return;
        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(statePath), ProtectionEntropy, DataProtectionScope.LocalMachine);
            var state = JsonSerializer.Deserialize<DeviceClockState>(clear, JsonOptions);
            if (state is not null && DateTime.TryParse(state.LastObservedUtc, out var last) && nowUtc < last.ToUniversalTime().AddMinutes(-10))
                throw new InvalidOperationException("The system clock was moved backwards. Correct the Windows date and time before continuing.");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The protected license state was copied from another computer or has been modified.", ex);
        }
    }

    private static void SaveClockState(DeviceLicensePayloadV2 payload, DateTime nowUtc)
    {
        var state = new DeviceClockState
        {
            DeviceId = payload.DeviceId,
            LicenseKey = payload.LicenseKey,
            LastObservedUtc = nowUtc.ToString("O")
        };
        var clear = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var protectedBytes = ProtectedData.Protect(clear, ProtectionEntropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(Path.Combine(AppBootstrap.AppDataPath, ClockStateFileName), protectedBytes);
    }
}

internal sealed class DeviceIdentityRecord
{
    public string InstallationId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string KeyName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

internal sealed class DeviceLicenseRequestV2
{
    public int Version { get; set; }
    public string RequestId { get; set; } = "";
    public string BusinessName { get; set; } = "";
    public string SubscriptionKey { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DevicePublicKey { get; set; } = "";
    public string FingerprintHash { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string Proof { get; set; } = "";

    public string SigningText()
        => string.Join("\n", Version, RequestId, BusinessName, SubscriptionKey, DeviceId, InstallationId, DeviceName,
            DevicePublicKey, FingerprintHash, CreatedUtc);
}

internal sealed class DeviceLicenseEnvelopeV2
{
    public int Version { get; set; }
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed class DeviceLicensePayloadV2
{
    public string LicenseKey { get; set; } = "";
    public int CustomerId { get; set; }
    public int LicenseId { get; set; }
    public string BusinessName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DevicePublicKey { get; set; } = "";
    public string Status { get; set; } = "Active";
    public int MaxDevices { get; set; }
    public int MaxStores { get; set; }
    public int MaxUsers { get; set; }
    public string IssuedUtc { get; set; } = "";
    public string ExpiresUtc { get; set; } = "";
    public string EncryptedConnectionKey { get; set; } = "";
    public string EncryptedConnection { get; set; } = "";
    public string ConnectionNonce { get; set; } = "";
    public string ConnectionTag { get; set; } = "";
}

internal sealed class DeviceClockState
{
    public string DeviceId { get; set; } = "";
    public string LicenseKey { get; set; } = "";
    public string LastObservedUtc { get; set; } = "";
}
