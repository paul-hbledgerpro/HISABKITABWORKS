using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class GmailOAuthService
{
    private const string ApplicationName = "HISAB KITAB WORKS";
    private const string OAuthClientFileName = "gmail-oauth-client.json";
    private const string ProtectedOAuthClientFileName = "gmail-oauth-client.dat";
    private static readonly string[] Scopes = { GmailService.Scope.GmailReadonly };
    private static readonly byte[] ClientEntropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-GMAIL-OAUTH-CLIENT-V1");

    private readonly string _protectedClientPath;
    private readonly string _bundledClientPath;
    private readonly ProtectedGoogleOAuthDataStore _tokenStore;

    public GmailOAuthService(IAppPaths paths)
    {
        _protectedClientPath = Path.Combine(paths.AppDataDirectory, ProtectedOAuthClientFileName);
        _bundledClientPath = Path.Combine(AppContext.BaseDirectory, OAuthClientFileName);
        _tokenStore = new ProtectedGoogleOAuthDataStore(
            Path.Combine(paths.AppDataDirectory, "gmail-oauth-tokens"));
    }

    public bool IsClientConfigured
        => File.Exists(_protectedClientPath) || File.Exists(_bundledClientPath);

    public string ConfigurationDescription
        => IsClientConfigured
            ? "Google sign-in setup is installed."
            : "Google sign-in needs the one-time OAuth setup file supplied by the developer.";

    public async Task ImportClientConfigurationAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Select the Google OAuth desktop client JSON file.", sourcePath);

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        try
        {
            ValidateClientConfiguration(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(_protectedClientPath)!);
            var protectedBytes = ProtectedData.Protect(
                bytes,
                ClientEntropy,
                DataProtectionScope.LocalMachine);
            await File.WriteAllBytesAsync(_protectedClientPath, protectedBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<string> ConnectAsync(
        string storeKey,
        string? loginHint,
        CancellationToken cancellationToken = default)
    {
        var secrets = await LoadClientSecretsAsync(cancellationToken);
        var userKey = BuildUserKey(storeKey);
        var initializer = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = Scopes,
            DataStore = _tokenStore,
            LoginHint = string.IsNullOrWhiteSpace(loginHint) ? null : loginHint.Trim(),
            Prompt = "consent",
            IncludeGrantedScopes = true
        };

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            initializer,
            Scopes,
            userKey,
            cancellationToken,
            _tokenStore);

        using var gmail = CreateGmailService(credential);
        var profile = await gmail.Users.GetProfile("me").ExecuteAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.EmailAddress))
            throw new InvalidOperationException("Google connected, but Gmail did not return the mailbox address.");
        return profile.EmailAddress.Trim();
    }

    public async Task DisconnectAsync(
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        var userKey = BuildUserKey(storeKey);
        try
        {
            var credential = await GetStoredCredentialAsync(storeKey, cancellationToken);
            if (credential is not null)
                await credential.RevokeTokenAsync(cancellationToken);
        }
        catch
        {
            // Local removal must still succeed if Google is temporarily
            // unreachable or the token was already revoked.
        }

        await _tokenStore.DeleteAsync<TokenResponse>(userKey);
    }

    public async Task<GmailService> GetConnectedServiceAsync(
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        var credential = await GetStoredCredentialAsync(storeKey, cancellationToken);
        if (credential is null)
            throw new InvalidOperationException(
                "This store is not connected to Gmail. Open Email Invoices and click CONNECT GMAIL.");

        if (credential.Token.IsStale && !await credential.RefreshTokenAsync(cancellationToken))
            throw new InvalidOperationException(
                "The saved Gmail authorization expired or was revoked. Click CONNECT GMAIL again.");

        return CreateGmailService(credential);
    }

    public async Task<bool> HasStoredAuthorizationAsync(
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        var token = await _tokenStore.GetAsync<TokenResponse>(BuildUserKey(storeKey));
        return token is not null
               && (!string.IsNullOrWhiteSpace(token.RefreshToken)
                   || !string.IsNullOrWhiteSpace(token.AccessToken));
    }

    private async Task<UserCredential?> GetStoredCredentialAsync(
        string storeKey,
        CancellationToken cancellationToken)
    {
        var userKey = BuildUserKey(storeKey);
        var token = await _tokenStore.GetAsync<TokenResponse>(userKey);
        if (token is null)
            return null;

        var secrets = await LoadClientSecretsAsync(cancellationToken);
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = Scopes,
            DataStore = _tokenStore
        });
        return new UserCredential(flow, userKey, token);
    }

    private async Task<ClientSecrets> LoadClientSecretsAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        if (File.Exists(_protectedClientPath))
        {
            var protectedBytes = await File.ReadAllBytesAsync(_protectedClientPath, cancellationToken);
            bytes = ProtectedData.Unprotect(
                protectedBytes,
                ClientEntropy,
                DataProtectionScope.LocalMachine);
        }
        else if (File.Exists(_bundledClientPath))
        {
            bytes = await File.ReadAllBytesAsync(_bundledClientPath, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException(
                "Google sign-in is not configured yet. Click IMPORT GOOGLE OAUTH SETUP and select "
                + "the Desktop app OAuth JSON file downloaded from Google Cloud.");
        }

        try
        {
            return ParseClientSecrets(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateClientConfiguration(byte[] bytes)
        => _ = ParseClientSecrets(bytes);

    private static ClientSecrets ParseClientSecrets(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        if (string.IsNullOrWhiteSpace(secrets.ClientId)
            || string.IsNullOrWhiteSpace(secrets.ClientSecret))
            throw new InvalidOperationException(
                "The selected JSON is not a valid Google OAuth Desktop app client file.");
        return secrets;
    }

    private static GmailService CreateGmailService(UserCredential credential)
        => new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });

    private static string BuildUserKey(string storeKey)
    {
        var normalized = string.IsNullOrWhiteSpace(storeKey)
            ? "DEFAULT"
            : storeKey.Trim().ToUpperInvariant();
        return $"invoice-email:{normalized}";
    }
}

internal sealed class ProtectedGoogleOAuthDataStore : IDataStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-GMAIL-OAUTH-TOKENS-V1");

    private readonly string _folder;

    public ProtectedGoogleOAuthDataStore(string folder)
    {
        _folder = folder;
    }

    public Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(_folder);
        var clear = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PathFor<T>(key), protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        if (!File.Exists(path))
            return Task.FromResult(default(T));

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var clear = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Task.FromResult(JsonSerializer.Deserialize<T>(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return Task.FromResult(default(T));
        }
    }

    public Task ClearAsync()
    {
        if (!Directory.Exists(_folder))
            return Task.CompletedTask;

        foreach (var file in Directory.EnumerateFiles(_folder, "*.dat", SearchOption.TopDirectoryOnly))
            File.Delete(file);
        return Task.CompletedTask;
    }

    private string PathFor<T>(string key)
    {
        var identity = $"{typeof(T).FullName}:{key}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_folder, $"{hash}.dat");
    }
}
