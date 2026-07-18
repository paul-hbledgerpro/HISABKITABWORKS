using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

/// <summary>
/// Talks only to the developer-controlled bank-sync gateway. Bank-provider
/// secrets and provider access tokens must never be stored in the desktop app.
/// The gateway is responsible for validating the signed subscription/PC,
/// creating the provider's hosted-link session, and calling Transactions Sync.
/// </summary>
internal sealed class LiveBankSyncClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public LiveBankSyncClient()
    {
        var serviceUrl = ResolveServiceUrl();
        IsConfigured = Uri.TryCreate(serviceUrl, UriKind.Absolute, out var baseUri)
                       && baseUri.Scheme == Uri.UriSchemeHttps;
        _http = new HttpClient
        {
            BaseAddress = IsConfigured ? baseUri : new Uri("https://localhost/"),
            Timeout = TimeSpan.FromSeconds(90)
        };
    }

    public bool IsConfigured { get; }

    public async Task<Uri> CreateHostedLinkAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            "/api/bank/link-session",
            CurrentIdentity());
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await ReadAsync<BankLinkSessionResponse>(response, cancellationToken);
        if (!Uri.TryCreate(payload.LinkUrl, UriKind.Absolute, out var linkUri)
            || linkUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The bank-link service returned an invalid hosted-link URL.");
        return linkUri;
    }

    public async Task<LiveBankSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            "/api/bank/transactions/sync",
            CurrentIdentity());
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<LiveBankSyncResult>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LiveBankConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var request = CreateSignedRequest(
            HttpMethod.Get,
            "/api/bank/connections",
            Array.Empty<byte>(),
            hasJsonBody: false);
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<List<LiveBankConnection>>(response, cancellationToken);
    }

    private static LiveBankClientIdentity CurrentIdentity()
    {
        var license = LicenseRuntime.CurrentLicense
                      ?? throw new InvalidOperationException("A valid PC license is required before connecting a bank.");
        var identity = DeviceLicenseService.GetOrCreateIdentity();
        return new LiveBankClientIdentity(
            LicenseRuntime.ActiveStoreGuid,
            license.CustomerId,
            license.LicenseId,
            identity.DeviceId,
            Environment.MachineName);
    }

    private HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string path, T payload)
    {
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return CreateSignedRequest(method, path, bodyBytes, hasJsonBody: true);
    }

    private HttpRequestMessage CreateSignedRequest(
        HttpMethod method,
        string path,
        byte[] bodyBytes,
        bool hasJsonBody)
    {
        var identity = CurrentIdentity();
        var proof = DeviceLicenseService.CreateGatewayRequestProof(
            method.Method,
            path,
            identity.StoreGuid,
            identity.CustomerId,
            identity.LicenseId,
            bodyBytes);
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (hasJsonBody)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        request.Headers.TryAddWithoutValidation("X-HK-Store-Guid", identity.StoreGuid);
        request.Headers.TryAddWithoutValidation("X-HK-Customer-Id", identity.CustomerId.ToString());
        request.Headers.TryAddWithoutValidation("X-HK-License-Id", identity.LicenseId.ToString());
        request.Headers.TryAddWithoutValidation("X-HK-Device-Id", proof.DeviceId);
        request.Headers.TryAddWithoutValidation("X-HK-Device-Name", proof.DeviceName);
        request.Headers.TryAddWithoutValidation("X-HK-Timestamp", proof.Timestamp);
        request.Headers.TryAddWithoutValidation("X-HK-Nonce", proof.Nonce);
        request.Headers.TryAddWithoutValidation("X-HK-Body-SHA256", proof.BodySha256);
        request.Headers.TryAddWithoutValidation("X-HK-Device-Proof", proof.DeviceProof);
        request.Headers.TryAddWithoutValidation("X-HK-License-Envelope", proof.LicenseEnvelope);
        request.Headers.TryAddWithoutValidation("X-HK-App-Version", Application.ProductVersion);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Live bank service returned {(int)response.StatusCode}. {AppBootstrap.RedactSensitiveText(message)}");
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Live bank service returned an empty response.");
    }

    public async Task EmailReportAsync(
        string recipient,
        string reportName,
        string period,
        string fileName,
        byte[] pdfBytes,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (pdfBytes.Length == 0 || pdfBytes.Length > 8_000_000)
            throw new InvalidOperationException("The report PDF must be smaller than 8 MB.");
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            "/api/reports/email",
            new ReportEmailRequest(
                recipient.Trim(),
                reportName.Trim(),
                period.Trim(),
                fileName,
                Convert.ToBase64String(pdfBytes)));
        using var response = await _http.SendAsync(request, cancellationToken);
        _ = await ReadAsync<ReportEmailResponse>(response, cancellationToken);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Live Bank Sync is ready in the app, but the developer bank-sync service URL is not configured. "
                + "Configure HISAB_KITAB_BANK_SYNC_URL after connecting a supported banking provider.");
    }

    private static string ResolveServiceUrl()
    {
        var environmentValue = Environment.GetEnvironmentVariable("HISAB_KITAB_BANK_SYNC_URL");
        if (!string.IsNullOrWhiteSpace(environmentValue))
            return environmentValue.Trim().TrimEnd('/') + "/";

        var endpointFile = Path.Combine(AppContext.BaseDirectory, "bank-sync-service.url");
        if (File.Exists(endpointFile))
        {
            var fileValue = File.ReadAllText(endpointFile).Trim();
            if (!string.IsNullOrWhiteSpace(fileValue))
                return fileValue.TrimEnd('/') + "/";
        }

        return "";
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record LiveBankClientIdentity(
    string StoreGuid,
    int CustomerId,
    int LicenseId,
    string DeviceId,
    string DeviceName);

internal sealed record BankLinkSessionResponse(string LinkUrl);

internal sealed record ReportEmailRequest(
    string Recipient,
    string ReportName,
    string Period,
    string FileName,
    string PdfBase64);

internal sealed record ReportEmailResponse(bool Sent, string MessageId);

internal sealed record LiveBankConnection(
    string ConnectionId,
    string Provider,
    string InstitutionName,
    string AccountName,
    string AccountMask,
    string Status,
    DateTime? LastSyncedUtc,
    string LastError);

internal sealed record LiveBankTransaction(
    string ExternalTransactionId,
    string ConnectionId,
    DateTime Date,
    string Description,
    decimal Credit,
    decimal Debit,
    string Category,
    string CheckNumber,
    string AccountName);

internal sealed record LiveBankSyncResult(
    IReadOnlyList<LiveBankConnection> Connections,
    IReadOnlyList<LiveBankTransaction> Added,
    IReadOnlyList<LiveBankTransaction> Modified,
    IReadOnlyList<string> RemovedTransactionIds,
    DateTime SyncedUtc);
