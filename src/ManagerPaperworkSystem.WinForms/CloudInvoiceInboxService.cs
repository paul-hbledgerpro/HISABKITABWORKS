using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record CloudInvoiceInboxSyncResult(
    int MessagesChecked,
    int PdfsDownloaded,
    int InvoicesImported,
    int DuplicatesSkipped,
    int NeedsReview,
    string ReviewFolder);

internal sealed class CloudInvoiceInboxService
{
    private readonly IAppPaths _paths;
    private readonly InvoiceEmailSyncService _importService;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public CloudInvoiceInboxService(IAppPaths paths, InvoiceEmailSyncService importService)
    {
        _paths = paths;
        _importService = importService;
    }

    public async Task TestAsync(
        LicensedBusinessConnection business,
        CancellationToken ct = default)
    {
        Validate(business);
        using var request = CreateRequest(business, HttpMethod.Get, "/api/store");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<CloudInvoiceInboxSyncResult> SyncAsync(
        LicensedBusinessConnection business,
        string storeKey,
        int storeId,
        int userId,
        string userName,
        CancellationToken ct = default)
    {
        Validate(business);
        using var listRequest = CreateRequest(
            business,
            HttpMethod.Get,
            "/api/invoices?status=pending_review&limit=100");
        using var listResponse = await _http.SendAsync(listRequest, ct);
        await EnsureSuccessAsync(listResponse, ct);
        var list = await listResponse.Content.ReadFromJsonAsync<InvoiceListResponse>(
                       cancellationToken: ct)
                   ?? new InvoiceListResponse();

        var inboxDirectory = Path.Combine(
            _paths.AppDataDirectory,
            "Invoice Cloud Inbox",
            SafeName(business.StoreGuid));
        Directory.CreateDirectory(inboxDirectory);

        var downloaded = 0;
        var imported = 0;
        var duplicates = 0;
        var needsReview = 0;
        var reviewFolder = "";

        foreach (var invoice in list.Invoices)
        {
            var localPaths = new List<string>();
            foreach (var attachment in invoice.Attachments)
            {
                ct.ThrowIfCancellationRequested();
                using var downloadRequest = CreateRequest(
                    business,
                    HttpMethod.Get,
                    attachment.DownloadPath);
                using var downloadResponse = await _http.SendAsync(downloadRequest, ct);
                await EnsureSuccessAsync(downloadResponse, ct);
                var safeFile = SafeName(attachment.FileName);
                var path = Path.Combine(
                    inboxDirectory,
                    $"{invoice.ReceivedUtc:yyyyMMdd_HHmmss}_{attachment.Id[..Math.Min(8, attachment.Id.Length)]}_{safeFile}");
                await using var file = File.Create(path);
                await downloadResponse.Content.CopyToAsync(file, ct);
                localPaths.Add(path);
                downloaded++;
            }

            if (localPaths.Count == 0)
                continue;

            var result = await _importService.ImportDownloadedPdfsAsync(
                storeKey,
                storeId,
                userId,
                userName,
                localPaths,
                ct);
            imported += result.InvoicesImported;
            duplicates += result.DuplicatesSkipped;
            needsReview += result.NeedsReview;
            reviewFolder = result.ReviewFolder;

            if (result.NeedsReview == 0
                && result.InvoicesImported + result.DuplicatesSkipped > 0)
            {
                using var statusRequest = CreateRequest(
                    business,
                    HttpMethod.Patch,
                    $"/api/invoices/{Uri.EscapeDataString(invoice.Id)}");
                statusRequest.Content = JsonContent.Create(new { status = "imported" });
                using var statusResponse = await _http.SendAsync(statusRequest, ct);
                await EnsureSuccessAsync(statusResponse, ct);
            }
        }

        return new CloudInvoiceInboxSyncResult(
            list.Invoices.Count,
            downloaded,
            imported,
            duplicates,
            needsReview,
            reviewFolder);
    }

    private static HttpRequestMessage CreateRequest(
        LicensedBusinessConnection business,
        HttpMethod method,
        string relativePath)
    {
        var request = new HttpRequestMessage(
            method,
            business.InvoiceInboxApiUrl.TrimEnd('/') + relativePath);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", business.InvoiceInboxApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void Validate(LicensedBusinessConnection business)
    {
        if (string.IsNullOrWhiteSpace(business.InvoiceInboxApiUrl)
            || string.IsNullOrWhiteSpace(business.InvoiceInboxAddress)
            || string.IsNullOrWhiteSpace(business.InvoiceInboxApiToken))
            throw new InvalidOperationException(
                "This store does not yet have a developer-provisioned HISAB KITAB invoice inbox. "
                + "Provision it in the Developer Account Manager and issue an updated PC license.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!string.IsNullOrWhiteSpace(error?.Error))
                throw new InvalidOperationException(error.Error);
        }
        catch (JsonException)
        {
            // Fall through to the safe HTTP status message.
        }

        throw new InvalidOperationException(
            $"The protected invoice service returned HTTP {(int)response.StatusCode}.");
    }

    private static string SafeName(string value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "invoice.pdf" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        return safe.Length <= 140 ? safe : safe[^140..];
    }

    private sealed class InvoiceListResponse
    {
        public List<CloudInvoice> Invoices { get; set; } = new();
    }

    private sealed class CloudInvoice
    {
        public string Id { get; set; } = "";
        public string From { get; set; } = "";
        public string Subject { get; set; } = "";
        public DateTime ReceivedUtc { get; set; }
        public List<CloudAttachment> Attachments { get; set; } = new();
    }

    private sealed class CloudAttachment
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DownloadPath { get; set; } = "";
    }

    private sealed record ErrorResponse(string Error);
}
