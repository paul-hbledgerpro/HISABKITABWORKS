using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal sealed record InvoiceInboxRemoteStore(
    string Id,
    string DisplayName,
    string StoreGuid,
    string EmailAddress,
    bool IsActive);

internal sealed record InvoiceInboxProvisionResult(
    InvoiceInboxRemoteStore Store,
    string ApiToken);

internal sealed class InvoiceInboxApiClient : IDisposable
{
    private readonly HttpClient _client;

    public InvoiceInboxApiClient(string baseUrl, string adminSecret)
    {
        if (!Uri.TryCreate(baseUrl?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !IsLocalhost(uri)))
            throw new InvalidOperationException("Enter a valid HTTPS Invoice Inbox service URL.");
        if (string.IsNullOrWhiteSpace(adminSecret))
            throw new InvalidOperationException("Enter the Invoice Inbox admin secret.");

        _client = new HttpClient
        {
            BaseAddress = new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminSecret.Trim());
    }

    public async Task<IReadOnlyList<InvoiceInboxRemoteStore>> LoadStoresAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("admin/stores", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<StoresResponse>(
            JsonOptions, cancellationToken);
        return payload?.Stores ?? [];
    }

    public async Task<InvoiceInboxProvisionResult> CreateStoreAsync(
        string displayName,
        string storeGuid,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            "admin/stores",
            new { displayName, storeGuid },
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadProvisionResultAsync(response, cancellationToken);
    }

    public async Task<InvoiceInboxProvisionResult> RotateStoreTokenAsync(
        InvoiceInboxRemoteStore store,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsync(
            $"admin/stores/{Uri.EscapeDataString(store.Id)}/rotate-token",
            content: null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<RotateResponse>(
            JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The Invoice Inbox service returned an empty response.");
        if (string.IsNullOrWhiteSpace(payload.ApiToken))
            throw new InvalidOperationException("The Invoice Inbox service did not return the new store token.");
        return new InvoiceInboxProvisionResult(store, payload.ApiToken);
    }

    public void Dispose() => _client.Dispose();

    private static async Task<InvoiceInboxProvisionResult> ReadProvisionResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadFromJsonAsync<CreateStoreResponse>(
            JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The Invoice Inbox service returned an empty response.");
        if (payload.Store is null || string.IsNullOrWhiteSpace(payload.ApiToken))
            throw new InvalidOperationException("The Invoice Inbox service did not return complete store credentials.");
        return new InvoiceInboxProvisionResult(payload.Store, payload.ApiToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = $"Invoice Inbox service returned HTTP {(int)response.StatusCode}.";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error))
                message = error.GetString() ?? message;
        }
        catch
        {
            // Retain the safe status-only message when the service did not return JSON.
        }
        throw new InvalidOperationException(message);
    }

    private static bool IsLocalhost(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record StoresResponse(IReadOnlyList<InvoiceInboxRemoteStore> Stores);
    private sealed record CreateStoreResponse(InvoiceInboxRemoteStore? Store, string ApiToken);
    private sealed record RotateResponse(string StoreId, string ApiToken);
}

internal static class InvoiceInboxCredentialProtector
{
    private const string Version = "v1";
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS|INVOICE-INBOX|DEVELOPER");

    public static string ProtectForWindowsUser(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value.Trim()),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectForWindowsUser(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var clearBytes = ProtectedData.Unprotect(
            Convert.FromBase64String(value),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clearBytes);
    }

    public static string ProtectStoreToken(string token, string adminSecret)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(adminSecret))
            throw new InvalidOperationException("The store token and admin secret are required.");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"HISAB-KITAB-WORKS|INVOICE-STORE-TOKEN|{adminSecret.Trim()}"));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[Encoding.UTF8.GetByteCount(token)];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(token), ciphertext, tag, Entropy);
        CryptographicOperations.ZeroMemory(key);
        return string.Join(':',
            Version,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext));
    }
}

internal sealed record InvoiceInboxDeveloperSettings(string BaseUrl, string ProtectedAdminSecret)
{
    public const string DefaultBaseUrl =
        "https://hisab-kitab-invoice-inbox.hbcommercesolutions.workers.dev";
}

internal static class InvoiceInboxDeveloperSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hisab Kitab",
        "Developer",
        "invoice_inbox_service.json");

    public static InvoiceInboxDeveloperSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new InvoiceInboxDeveloperSettings(
                    InvoiceInboxDeveloperSettings.DefaultBaseUrl, "");
            return JsonSerializer.Deserialize<InvoiceInboxDeveloperSettings>(
                       File.ReadAllText(SettingsPath))
                   ?? new InvoiceInboxDeveloperSettings(
                       InvoiceInboxDeveloperSettings.DefaultBaseUrl, "");
        }
        catch
        {
            return new InvoiceInboxDeveloperSettings(
                InvoiceInboxDeveloperSettings.DefaultBaseUrl, "");
        }
    }

    public static void Save(string baseUrl, string adminSecret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var settings = new InvoiceInboxDeveloperSettings(
            baseUrl.Trim().TrimEnd('/'),
            InvoiceInboxCredentialProtector.ProtectForWindowsUser(adminSecret));
        File.WriteAllText(
            SettingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
