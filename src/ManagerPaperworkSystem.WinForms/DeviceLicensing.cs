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

    public static bool HasService(string service)
    {
        if (string.Equals(service, "Accounting", StringComparison.OrdinalIgnoreCase))
            return true;
        var enabled = CurrentLicense?.EnabledServices ?? "";
        return enabled.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(value, service, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class DeviceLicenseService
{
    // Matching private keys remain only on authorized developer PCs.
    internal const string CurrentLicenseSigningPublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyJC7f5wQ5REEWdHzKuqXQVU4NjY8t17V3IHj9Ahd597HRhY6HZBxnzso1mIp0fzB8ZWu/Xgnvi2scepKCFnscVKoLaLSEQpanWtDHdA4sMCfveNJ9W/Tj54lgbt89mGaGNcteqr7L0elBSSzPyJxRLKUMbWD29D5fqkpa/tMFevwVfDAzBY2w9qbQL1cj2Y1in86q91oZOUYhaEFns4c6pYJ7Tm/G8pP8nQYXaP7El/m9hPFM3XIXGAh7O01+7ottIpacGfSOGkwa7Nufv+IbQnc1RKtqKg3/U3XLPllyfQNZyJ8n3RoVjwaXtTDPs1AACGFLnCuB2HSocNarphK5xKk5E5oeF/YvOI0EGYXzPl5Hs/ExvjJuJm1bhxFRBcIWFEAba7hH+JrPv6RIpEFHr/xWbqZagbRjSr5zRi8GkcG5KDJdOER6NP8ErNaIhOEiyPuPeW9VXzn4ch5s+BOxtyzGvYiXiht5yytpcXvEvK8t9L0issM5fuXRCD2/v/pAgMBAAE=";
    internal const string LegacyLicenseSigningPublicKeyBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA41Pt4R+4COqv01HNi5KRVe+Ws0yQjhcaj19XgXO7kZiXjSYOqjaqPPrGDnW93Q/tk5boAic+YyxhaVtEJ4AF9BONKUGmamKKc3Y4M9vO/kZAr3n7t2/h3EVNVoJUWL4Xpe0FL8+Ehr3tbejVayBCZ5xsrrzdzXFRE2CTlP6dFQP9TFsQGzceZu7EIStttZ/VEZcmQQ++BSPgqv41qlfIulU9ufeDDYpi6s4KJQkZIUzcrxVhGdhfBvPE7yELQYn7pXlpvSZfeWuIbFoc1DxpGYmJlQktam6kDUgp/QnKe//V+N5eW0vJM40RnwhxAyiNylbB8ie++QlWgZlac2XL2lAHDrvUOJahsB7G06qTgu8yx17bH27o68V2YZiuLVNpY44ofB1VFn0aadK+rHxvMiQeZ4gC8fauP/5f28R+Iw1H/YM1oIwXOekkaZS+J0HtYje3Sddu+H0V8/tBA0yKHjNxPRiWrxTYdlNv0vFJ1WpLx1u8UTbQBoj7b2Nqg8aRAgMBAAE=";
    private static readonly string[] TrustedLicenseSigningPublicKeys =
    [
        CurrentLicenseSigningPublicKeyBase64,
        LegacyLicenseSigningPublicKeyBase64
    ];

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

    public static DeviceLicenseRequestV2 CreateRequest(
        string businessName,
        string storeGuid,
        string storeZip,
        string storeState = "",
        string businessType = "",
        string subscriptionKey = "")
    {
        if (string.IsNullOrWhiteSpace(businessName))
            throw new InvalidOperationException("Enter the store name before creating the activation request.");
        if (string.IsNullOrWhiteSpace(storeGuid))
            throw new InvalidOperationException("Enter the Store GUID (the store database name).");
        if (string.IsNullOrWhiteSpace(storeZip))
            throw new InvalidOperationException("Enter the store ZIP code.");
        if (!StoreGuidFormat.IsValid(storeGuid.Trim()))
            throw new InvalidOperationException("Store GUID must use STATE_STORENAME_BUSINESSTYPE_ZIP format.");
        if (!string.IsNullOrWhiteSpace(storeState) || !string.IsNullOrWhiteSpace(businessType))
        {
            var expectedGuid = StoreGuidFormat.Create(storeState, businessName, businessType, storeZip);
            if (!string.Equals(expectedGuid, storeGuid.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException($"Store GUID must be {expectedGuid} for the entered store details.");
        }

        var identity = GetOrCreateIdentity();
        var request = new DeviceLicenseRequestV2
        {
            Version = 3,
            RequestId = Guid.NewGuid().ToString("N"),
            BusinessName = businessName.Trim(),
            SubscriptionKey = subscriptionKey.Trim(),
            StoreGuid = storeGuid.Trim(),
            StoreZip = storeZip.Trim(),
            StoreState = storeState.Trim().ToUpperInvariant(),
            BusinessType = businessType.Trim().ToUpperInvariant(),
            AppVersion = Application.ProductVersion,
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

    public static string CreateRequestText(
        string businessName,
        string storeGuid,
        string storeZip,
        string storeState = "",
        string businessType = "",
        string subscriptionKey = "")
        => ActivationCodeCodec.FormatRequest(CreateRequest(
            businessName, storeGuid, storeZip, storeState, businessType, subscriptionKey));

    public static void ExportRequest(
        string path,
        string businessName,
        string storeGuid,
        string storeZip,
        string storeState = "",
        string businessType = "",
        string subscriptionKey = "")
    {
        var request = CreateRequest(
            businessName, storeGuid, storeZip, storeState, businessType, subscriptionKey);
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
            if (validation.Payload is not null)
                LicensedBusinessService.SaveFromLicense(validation.Payload);
            return validation;
        }
        catch (Exception ex)
        {
            LicenseRuntime.CurrentLicense = null;
            LicenseRuntime.IsReadOnly = false;
            return new(DeviceLicenseStatus.Invalid, ex.Message);
        }
    }

    public static DeviceLicenseValidation InstallLicense(
        string sourcePath,
        IReadOnlyCollection<string>? requiredExistingDatabases = null,
        string expectedAddedDatabase = "",
        string expectedAddedBusinessName = "")
    {
        var validation = ValidateLicenseFile(sourcePath, allowExpired: false, updateClockState: false);
        if (validation.Status != DeviceLicenseStatus.Valid || validation.Payload is null)
            return validation;

        EnsureExistingBusinessesRemainLicensed(validation.Payload, requiredExistingDatabases);
        EnsureRequestedBusinessIsLicensed(
            validation.Payload, expectedAddedDatabase, expectedAddedBusinessName);

        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        File.Copy(sourcePath, InstalledLicensePath, true);
        SaveProtectedConnection(validation.Payload);
        LicensedBusinessService.SaveFromLicense(validation.Payload);
        SaveLicenseSummary(validation.Payload);
        SaveClockState(validation.Payload, DateTime.UtcNow);
        LicenseRuntime.CurrentLicense = validation.Payload;
        LicenseRuntime.IsReadOnly = false;
        return validation;
    }

    public static DeviceLicenseValidation InstallLicenseCode(
        string activationText,
        string expectedBusinessName,
        string expectedStoreGuid,
        string expectedStoreZip,
        IReadOnlyCollection<string>? requiredExistingDatabases = null,
        bool addingLicensedStore = false)
    {
        var licenseJson = ActivationCodeCodec.DecodeLicenseJson(activationText);
        var validation = ValidateLicenseJson(licenseJson, allowExpired: false, updateClockState: false);
        if (validation.Status != DeviceLicenseStatus.Valid || validation.Payload is null)
            return validation;
        var payload = validation.Payload;
        if (addingLicensedStore)
        {
            EnsureRequestedBusinessIsLicensed(payload, expectedStoreGuid, expectedBusinessName);
        }
        else
        {
            if (!string.Equals(payload.BusinessName, expectedBusinessName.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This license key belongs to a different store name.");
            if (!string.Equals(payload.StoreGuid, expectedStoreGuid.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This license key belongs to a different Store GUID/database.");
            if (!string.Equals(payload.StoreZip, expectedStoreZip.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This license key belongs to a different ZIP code.");
        }

        EnsureExistingBusinessesRemainLicensed(payload, requiredExistingDatabases);

        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        File.WriteAllText(InstalledLicensePath, licenseJson);
        SaveProtectedConnection(payload);
        LicensedBusinessService.SaveFromLicense(payload);
        SaveLicenseSummary(payload);
        SaveClockState(payload, DateTime.UtcNow);
        LicenseRuntime.CurrentLicense = payload;
        LicenseRuntime.IsReadOnly = false;
        return validation;
    }

    private static void EnsureExistingBusinessesRemainLicensed(
        DeviceLicensePayloadV2 payload,
        IReadOnlyCollection<string>? requiredExistingDatabases)
    {
        if (requiredExistingDatabases is null || requiredExistingDatabases.Count == 0)
            return;

        var updatedDatabases = payload.Businesses
            .Select(x => x.DatabaseName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requiredExistingDatabases
            .Where(x => !updatedDatabases.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                "The updated license does not include every store already licensed on this PC. " +
                "Ask the developer to keep the existing stores and add the new store before generating the updated key.");
    }

    private static void EnsureRequestedBusinessIsLicensed(
        DeviceLicensePayloadV2 payload,
        string expectedDatabase,
        string expectedBusinessName)
    {
        if (string.IsNullOrWhiteSpace(expectedDatabase))
            return;

        if (!payload.Businesses.Any(x =>
                string.Equals(
                    string.IsNullOrWhiteSpace(x.StoreGuid) ? x.DatabaseName : x.StoreGuid,
                    expectedDatabase.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.BusinessName, expectedBusinessName.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "The updated license does not contain the new store requested on this PC. " +
                "Ask the developer to add this Store GUID to the existing client subscription and generate the key again.");
    }

    public static DeviceLicenseValidation ValidateLicenseFile(string path, bool allowExpired, bool updateClockState)
    {
        return ValidateLicenseJson(File.ReadAllText(path), allowExpired, updateClockState);
    }

    private static DeviceLicenseValidation ValidateLicenseJson(string licenseJson, bool allowExpired, bool updateClockState)
    {
        var envelope = JsonSerializer.Deserialize<DeviceLicenseEnvelopeV2>(licenseJson, JsonOptions)
            ?? throw new InvalidOperationException("The selected license file is not valid.");
        if (envelope.Version != 2)
            throw new InvalidOperationException("This is not a version-2 device license. Export a new PC request and issue a device-bound license.");

        var payloadBytes = Convert.FromBase64String(envelope.Payload);
        var signatureBytes = Convert.FromBase64String(envelope.Signature);
        var signatureValid = false;
        foreach (var trustedPublicKey in TrustedLicenseSigningPublicKeys)
        {
            using var signer = RSA.Create();
            signer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trustedPublicKey), out _);
            if (!signer.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                continue;
            signatureValid = true;
            break;
        }
        if (!signatureValid)
            throw new InvalidOperationException("The license signature is invalid. The file was not issued by an authorized License Generator.");

        var payload = JsonSerializer.Deserialize<DeviceLicensePayloadV2>(payloadBytes, JsonOptions)
            ?? throw new InvalidOperationException("The license payload is missing.");
        if (string.IsNullOrWhiteSpace(payload.BusinessName) || string.IsNullOrWhiteSpace(payload.StoreGuid))
            throw new InvalidOperationException("The license is missing the Store Name or Store GUID.");
        if (payload.Businesses.Count > 0 && !payload.Businesses.Any(business =>
                string.Equals(
                    string.IsNullOrWhiteSpace(business.StoreGuid) ? business.DatabaseName : business.StoreGuid,
                    payload.StoreGuid, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Store GUID does not match an approved database in this license.");
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
        var settings = DecryptConnectionPayload(
            payload.EncryptedConnectionKey,
            payload.EncryptedConnection,
            payload.ConnectionNonce,
            payload.ConnectionTag);
        var clear = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var protectedBytes = ProtectedData.Protect(clear, ProtectionEntropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(ProtectedConnectionPath, protectedBytes);
        CryptographicOperations.ZeroMemory(clear);
    }

    internal static DatabaseConnectionSettings DecryptConnectionPayload(
        string encryptedConnectionKey,
        string encryptedConnection,
        string connectionNonce,
        string connectionTag)
    {
        var identity = GetOrCreateIdentity();
        using var key = OpenKey(identity);
        using var rsa = new RSACng(key);
        var aesKey = rsa.Decrypt(Convert.FromBase64String(encryptedConnectionKey), RSAEncryptionPadding.OaepSHA256);
        var cipher = Convert.FromBase64String(encryptedConnection);
        var nonce = Convert.FromBase64String(connectionNonce);
        var tag = Convert.FromBase64String(connectionTag);
        var clear = new byte[cipher.Length];
        try
        {
            using (var aes = new AesGcm(aesKey, tag.Length))
                aes.Decrypt(nonce, cipher, tag, clear);
            var settings = JsonSerializer.Deserialize<DatabaseConnectionSettings>(clear, JsonOptions)
                ?? throw new InvalidOperationException("The encrypted database connection is invalid.");
            if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Database))
                throw new InvalidOperationException("The device license does not contain a usable database connection.");
            return settings;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    private static void SaveLicenseSummary(DeviceLicensePayloadV2 payload)
    {
        File.WriteAllText(AppBootstrap.LicenseFilePath, JsonSerializer.Serialize(new
        {
            Version = 2,
            payload.ActivationId,
            payload.LicenseKey,
            payload.BusinessName,
            payload.StoreGuid,
            payload.StoreZip,
            payload.StoreState,
            payload.BusinessType,
            payload.DeviceId,
            payload.DeviceName,
            payload.MaxDevices,
            payload.MaxStores,
            payload.MaxUsers,
            ApprovedBusinesses = payload.Businesses.Count > 0 ? payload.Businesses.Count : 1,
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
    public string StoreGuid { get; set; } = "";
    public string StoreZip { get; set; } = "";
    public string StoreState { get; set; } = "";
    public string BusinessType { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DevicePublicKey { get; set; } = "";
    public string FingerprintHash { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string Proof { get; set; } = "";

    public string SigningText()
    {
        var original = string.Join("\n", Version, RequestId, BusinessName, SubscriptionKey, StoreGuid, StoreZip, AppVersion, DeviceId, InstallationId, DeviceName,
            DevicePublicKey, FingerprintHash, CreatedUtc);
        return Version >= 3 ? string.Join("\n", original, StoreState, BusinessType) : original;
    }
}

internal sealed class DeviceLicenseEnvelopeV2
{
    public int Version { get; set; }
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed class DeviceLicensePayloadV2
{
    public string ActivationId { get; set; } = "";
    public string LicenseKey { get; set; } = "";
    public int CustomerId { get; set; }
    public int LicenseId { get; set; }
    public string BusinessName { get; set; } = "";
    public string StoreGuid { get; set; } = "";
    public string StoreZip { get; set; } = "";
    public string StoreState { get; set; } = "";
    public string BusinessType { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DevicePublicKey { get; set; } = "";
    public string Status { get; set; } = "Active";
    public int MaxDevices { get; set; }
    public int MaxStores { get; set; }
    public int MaxUsers { get; set; }
    public string EnabledServices { get; set; } = "Accounting";
    public string IssuedUtc { get; set; } = "";
    public string ExpiresUtc { get; set; } = "";
    public string EncryptedConnectionKey { get; set; } = "";
    public string EncryptedConnection { get; set; } = "";
    public string ConnectionNonce { get; set; } = "";
    public string ConnectionTag { get; set; } = "";
    public List<LicensedBusinessPayloadV1> Businesses { get; set; } = new();
}

internal sealed class LicensedBusinessPayloadV1
{
    public int BusinessId { get; set; }
    public string BusinessName { get; set; } = "";
    public string Address { get; set; } = "";
    public string StoreGuid { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public bool IsPrimary { get; set; }
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
