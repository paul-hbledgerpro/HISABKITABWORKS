using System.Security.Cryptography;
using System.Text;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

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
    public List<LicensedBusinessPayloadV1> Businesses { get; set; } = new();
}

internal sealed class DeviceConnectionPayload
{
    public string DatabaseType { get; set; } = "SqlServer";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}

internal sealed class LicensedBusinessPayloadV1
{
    public int BusinessId { get; set; }
    public string BusinessName { get; set; } = "";
    public string Address { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public bool IsPrimary { get; set; }
    public string EncryptedConnectionKey { get; set; } = "";
    public string EncryptedConnection { get; set; } = "";
    public string ConnectionNonce { get; set; } = "";
    public string ConnectionTag { get; set; } = "";
}

internal static class DeviceRequestValidator
{
    public static void Validate(DeviceLicenseRequestV2 request)
    {
        if (request.Version != 2 || string.IsNullOrWhiteSpace(request.BusinessName) ||
            string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.DevicePublicKey))
            throw new InvalidOperationException("The PC request is incomplete or uses an unsupported version.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.SubscriptionKey, @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$"))
            throw new InvalidOperationException("The PC request does not contain a valid subscription key.");

        var publicKey = Convert.FromBase64String(request.DevicePublicKey);
        var hex = Convert.ToHexString(SHA256.HashData(publicKey));
        var expectedDeviceId = $"HKD-{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
        if (!string.Equals(request.DeviceId, expectedDeviceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The PC request Device ID does not match its public key.");

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (!rsa.VerifyData(Encoding.UTF8.GetBytes(request.SigningText()), Convert.FromBase64String(request.Proof),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidOperationException("The PC request proof is invalid or the request was modified.");
    }
}
