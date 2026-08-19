using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed record InvoiceInboxLicenseProvisioning(
    string ApiBaseUrl,
    string InvoiceAddress,
    string StoreApiToken);

internal static class InvoiceInboxLicenseProvisioningLoader
{
    private static readonly byte[] DeveloperEntropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS|INVOICE-INBOX|DEVELOPER");
    private static readonly byte[] TokenEntropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS|INVOICE-INBOX|DEVELOPER");

    public static InvoiceInboxLicenseProvisioning? Load(
        string licensingConnectionString,
        int customerId,
        string storeGuid)
    {
        try
        {
            using var connection = new SqlConnection(licensingConnectionString);
            connection.Open();
            using var command = new SqlCommand(@"
IF OBJECT_ID('dbo.InvoiceInboxProvisioning', 'U') IS NOT NULL
BEGIN
    SELECT ApiBaseUrl, InvoiceAddress, EncryptedStoreApiToken
    FROM dbo.InvoiceInboxProvisioning
    WHERE CustomerId=@customer AND StoreGuid=@guid;
END", connection);
            command.Parameters.AddWithValue("@customer", customerId);
            command.Parameters.AddWithValue("@guid", storeGuid.Trim().ToUpperInvariant());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var encryptedToken = reader.GetString(2);
            var token = UnprotectStoreToken(encryptedToken);
            if (string.IsNullOrWhiteSpace(token))
                return null;

            return new InvoiceInboxLicenseProvisioning(
                reader.GetString(0).Trim().TrimEnd('/'),
                reader.GetString(1).Trim().ToLowerInvariant(),
                token);
        }
        catch (SqlException ex) when (ex.Number is 208 or 207)
        {
            return null;
        }
    }

    private static string UnprotectStoreToken(string protectedValue)
    {
        var parts = protectedValue.Split(':');
        if (parts.Length != 4 || !parts[0].Equals("v1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The store invoice token uses an unsupported protection format. Reprovision the store inbox.");

        var settings = LoadDeveloperSettings();
        if (string.IsNullOrWhiteSpace(settings.ProtectedAdminSecret))
            throw new InvalidOperationException(
                "Open the Developer Account Manager and save the Invoice Inbox admin secret before issuing this license.");

        var protectedSecret = Convert.FromBase64String(settings.ProtectedAdminSecret);
        var secretBytes = ProtectedData.Unprotect(
            protectedSecret,
            DeveloperEntropy,
            DataProtectionScope.CurrentUser);
        var adminSecret = Encoding.UTF8.GetString(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"HISAB-KITAB-WORKS|INVOICE-STORE-TOKEN|{adminSecret.Trim()}"));
        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var ciphertext = Convert.FromBase64String(parts[3]);
        var clear = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, clear, TokenEntropy);
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static DeveloperSettings LoadDeveloperSettings()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hisab Kitab",
            "Developer",
            "invoice_inbox_service.json");
        if (!File.Exists(path))
            return new DeveloperSettings("", "");
        return JsonSerializer.Deserialize<DeveloperSettings>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? new DeveloperSettings("", "");
    }

    private sealed record DeveloperSettings(string BaseUrl, string ProtectedAdminSecret);
}
