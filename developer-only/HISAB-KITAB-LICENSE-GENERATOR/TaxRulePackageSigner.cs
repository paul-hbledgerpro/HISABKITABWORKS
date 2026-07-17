using System.Security.Cryptography;
using System.Text.Json;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal static class TaxRulePackageSigner
{
    public static void Sign(string inputJsonPath, string outputPackagePath)
    {
        if (!File.Exists(inputJsonPath))
            throw new FileNotFoundException("The tax-rule JSON file was not found.", inputJsonPath);
        var payload = File.ReadAllBytes(inputJsonPath);
        ValidatePayload(payload);

        using var signer = RSA.Create();
        signer.ImportRSAPrivateKey(Convert.FromBase64String(SigningKeyStore.Load()), out _);
        var signature = signer.SignData(
            payload,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var envelope = new SignedTaxRuleEnvelope
        {
            Version = 1,
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(signature)
        };
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPackagePath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(
            outputPackagePath,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidatePayload(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 1 ||
            string.IsNullOrWhiteSpace(root.GetProperty("RuleSetId").GetString()) ||
            string.IsNullOrWhiteSpace(root.GetProperty("Version").GetString()))
            throw new InvalidOperationException("The tax-rule package identity is invalid.");
        var states = root.GetProperty("States").EnumerateArray().ToList();
        if (states.Count != 51 ||
            states.Select(state => state.GetProperty("StateCode").GetString()?.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != 51)
            throw new InvalidOperationException(
                "The tax-rule package must contain all 50 states and the District of Columbia.");
    }

    private sealed class SignedTaxRuleEnvelope
    {
        public int Version { get; set; }
        public string Payload { get; set; } = "";
        public string Signature { get; set; } = "";
    }
}
