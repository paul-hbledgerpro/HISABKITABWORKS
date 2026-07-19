using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.UI.Services;
using MimeKit;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class InvoiceEmailSyncSettings
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Gmail";
    public string ImapServer { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string EmailAddress { get; set; } = "";
    public string PasswordOrAppPassword { get; set; } = "";
    public string MailFolder { get; set; } = "INBOX";
    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public List<string> ProcessedAttachmentHashes { get; set; } = new();
}

internal sealed record InvoiceEmailSyncResult(
    int MessagesChecked,
    int AttachmentsFound,
    int InvoicesImported,
    int DuplicatesSkipped,
    int NeedsReview,
    string ReviewFolder);

internal sealed class InvoiceEmailSyncService
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-INVOICE-EMAIL-SYNC-V1");

    private readonly IAppPaths _paths;
    private readonly InvoiceImportService _invoiceImporter;
    private readonly PurchaseService _purchaseService;
    private readonly string _settingsPath;

    public InvoiceEmailSyncService(
        IAppPaths paths,
        InvoiceImportService invoiceImporter,
        PurchaseService purchaseService)
    {
        _paths = paths;
        _invoiceImporter = invoiceImporter;
        _purchaseService = purchaseService;
        _settingsPath = Path.Combine(_paths.AppDataDirectory, "invoice-email-sync.dat");
    }

    public InvoiceEmailSyncSettings GetSettings(string storeKey)
    {
        var all = LoadAll();
        return all.TryGetValue(NormalizeStoreKey(storeKey), out var settings)
            ? settings
            : new InvoiceEmailSyncSettings();
    }

    public void SaveSettings(string storeKey, InvoiceEmailSyncSettings settings)
    {
        var all = LoadAll();
        all[NormalizeStoreKey(storeKey)] = settings;
        SaveAll(all);
    }

    public bool IsDue(string storeKey, TimeSpan minimumInterval)
    {
        var settings = GetSettings(storeKey);
        return settings.Enabled
               && !string.IsNullOrWhiteSpace(settings.EmailAddress)
               && !string.IsNullOrWhiteSpace(settings.PasswordOrAppPassword)
               && (settings.LastSuccessfulSyncUtc is null
                   || DateTime.UtcNow - settings.LastSuccessfulSyncUtc.Value >= minimumInterval);
    }

    public async Task TestConnectionAsync(InvoiceEmailSyncSettings settings, CancellationToken ct = default)
    {
        ValidateSettings(settings);
        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, settings, ct);
        var folder = await GetFolderAsync(client, settings.MailFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task<InvoiceEmailSyncResult> SyncAsync(
        string storeKey,
        int storeId,
        int userId,
        string userName,
        DateOnly? invoiceMonth = null,
        CancellationToken ct = default)
    {
        var settings = GetSettings(storeKey);
        ValidateSettings(settings);

        var safeStoreKey = MakeSafeFileName(NormalizeStoreKey(storeKey));
        var inboxFolder = Path.Combine(_paths.AppDataDirectory, "Invoice Email Inbox", safeStoreKey);
        var reviewFolder = Path.Combine(inboxFolder, "Needs Review");
        Directory.CreateDirectory(inboxFolder);
        Directory.CreateDirectory(reviewFolder);

        var processed = settings.ProcessedAttachmentHashes
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingInvoices = await _purchaseService.GetInvoicesAsync(storeId, ct);
        var existingKeys = existingInvoices
            .Select(invoice => InvoiceKey(invoice.VendorName, invoice.InvoiceNumber))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var messagesChecked = 0;
        var attachmentsFound = 0;
        var importedCount = 0;
        var duplicateCount = 0;
        var reviewCount = 0;

        using var client = new ImapClient();
        await ConnectAndAuthenticateAsync(client, settings, ct);
        var folder = await GetFolderAsync(client, settings.MailFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var monthStart = invoiceMonth.HasValue
            ? new DateOnly(invoiceMonth.Value.Year, invoiceMonth.Value.Month, 1)
            : (DateOnly?)null;
        var monthEnd = monthStart?.AddMonths(1);
        SearchQuery searchQuery = monthStart.HasValue
            ? SearchQuery.DeliveredAfter(monthStart.Value.AddDays(-7).ToDateTime(TimeOnly.MinValue))
                .And(SearchQuery.DeliveredBefore(monthEnd!.Value.AddDays(14).ToDateTime(TimeOnly.MinValue)))
            : SearchQuery.DeliveredAfter(
                (settings.LastSuccessfulSyncUtc ?? DateTime.UtcNow.AddDays(-45)).AddDays(-2));
        var ids = await folder.SearchAsync(searchQuery, ct);
        var idsToProcess = monthStart.HasValue
            ? ids.OrderBy(uid => uid.Id)
            : ids.OrderByDescending(uid => uid.Id).Take(250).OrderBy(uid => uid.Id);
        foreach (var id in idsToProcess)
        {
            ct.ThrowIfCancellationRequested();
            var message = await folder.GetMessageAsync(id, ct);
            messagesChecked++;

            foreach (var attachment in message.Attachments.OfType<MimePart>())
            {
                var fileName = attachment.FileName ?? attachment.ContentDisposition?.FileName ?? "";
                if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || attachment.Content is null)
                    continue;

                attachmentsFound++;
                await using var memory = new MemoryStream();
                await attachment.Content.DecodeToAsync(memory, ct);
                var bytes = memory.ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!monthStart.HasValue && processed.Contains(hash))
                {
                    duplicateCount++;
                    continue;
                }

                var received = message.Date == DateTimeOffset.MinValue
                    ? DateTime.Now
                    : message.Date.LocalDateTime;
                var storedName = $"{received:yyyyMMdd_HHmmss}_{hash[..12]}_{MakeSafeFileName(fileName)}";
                var storedPath = Path.Combine(inboxFolder, storedName);
                await File.WriteAllBytesAsync(storedPath, bytes, ct);

                var parseResult = await _invoiceImporter.ImportAsync(storedPath, ct: ct);
                var invoices = parseResult.Invoices.Count > 0
                    ? parseResult.Invoices
                    : new List<ImportedInvoice>
                    {
                        new()
                        {
                            VendorName = parseResult.VendorName,
                            InvoiceNumber = parseResult.InvoiceNumber,
                            InvoiceDate = parseResult.InvoiceDate,
                            Total = parseResult.Total,
                            Lines = parseResult.Lines
                        }
                    };

                var candidateInvoices = monthStart.HasValue
                    ? invoices
                        .Where(invoice => invoice.InvoiceDate.HasValue
                                          && invoice.InvoiceDate.Value >= monthStart.Value
                                          && invoice.InvoiceDate.Value < monthEnd!.Value)
                        .ToList()
                    : invoices;

                if (monthStart.HasValue && candidateInvoices.Count == 0)
                    continue;

                if (!parseResult.Success
                    || candidateInvoices.Count == 0
                    || candidateInvoices.Any(invoice => !IsVerified(invoice)))
                {
                    MoveToReview(storedPath, reviewFolder);
                    processed.Add(hash);
                    reviewCount++;
                    continue;
                }

                var importedFromAttachment = 0;
                foreach (var invoice in candidateInvoices)
                {
                    var key = InvoiceKey(invoice.VendorName, invoice.InvoiceNumber);
                    if (existingKeys.Contains(key))
                    {
                        duplicateCount++;
                        continue;
                    }

                    var lines = invoice.Lines ?? new List<PurchaseInvoiceLine>();
                    await _purchaseService.AddInvoiceAsync(
                        storeId,
                        invoice.InvoiceDate!.Value,
                        null,
                        invoice.VendorName,
                        invoice.InvoiceNumber,
                        invoice.Total!.Value,
                        $"Automatically imported from {message.From.Mailboxes.FirstOrDefault()?.Address ?? "client email"}; subject: {message.Subject}",
                        storedPath,
                        lines,
                        userId,
                        userName,
                        ct);
                    existingKeys.Add(key);
                    importedCount++;
                    importedFromAttachment++;
                }

                if (importedFromAttachment > 0 || candidateInvoices.All(invoice =>
                        existingKeys.Contains(InvoiceKey(invoice.VendorName, invoice.InvoiceNumber))))
                    processed.Add(hash);
            }
        }

        await client.DisconnectAsync(true, ct);
        if (!monthStart.HasValue)
            settings.LastSuccessfulSyncUtc = DateTime.UtcNow;
        settings.ProcessedAttachmentHashes = processed.TakeLast(5000).ToList();
        SaveSettings(storeKey, settings);

        return new InvoiceEmailSyncResult(
            messagesChecked,
            attachmentsFound,
            importedCount,
            duplicateCount,
            reviewCount,
            reviewFolder);
    }

    private static bool IsVerified(ImportedInvoice invoice)
        => !string.IsNullOrWhiteSpace(invoice.VendorName)
           && !invoice.VendorName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
           && invoice.InvoiceDate is not null
           && invoice.Total is > 0m
           && invoice.Lines.Count > 0
           && invoice.Lines.All(line =>
               !string.IsNullOrWhiteSpace(line.ProductName)
               && line.Quantity > 0m
               && line.UnitCost > 0m);

    private static void ValidateSettings(InvoiceEmailSyncSettings settings)
    {
        if (!settings.Enabled)
            throw new InvalidOperationException("Automatic invoice email sync is not enabled for this store.");
        if (string.IsNullOrWhiteSpace(settings.ImapServer))
            throw new InvalidOperationException("Enter the IMAP server.");
        if (settings.ImapPort is < 1 or > 65535)
            throw new InvalidOperationException("Enter a valid IMAP port.");
        if (string.IsNullOrWhiteSpace(settings.EmailAddress))
            throw new InvalidOperationException("Enter the client email address.");
        if (string.IsNullOrWhiteSpace(settings.PasswordOrAppPassword))
            throw new InvalidOperationException("Enter the email app password.");
    }

    private static async Task ConnectAndAuthenticateAsync(
        ImapClient client,
        InvoiceEmailSyncSettings settings,
        CancellationToken ct)
    {
        var socketOptions = settings.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(settings.ImapServer.Trim(), settings.ImapPort, socketOptions, ct);
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        await client.AuthenticateAsync(
            settings.EmailAddress.Trim(),
            settings.PasswordOrAppPassword.Replace(" ", "", StringComparison.Ordinal),
            ct);
    }

    private static async Task<IMailFolder> GetFolderAsync(
        ImapClient client,
        string folderName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderName)
            || folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return client.Inbox;

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        return await root.GetSubfolderAsync(folderName.Trim(), ct);
    }

    private Dictionary<string, InvoiceEmailSyncSettings> LoadAll()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new Dictionary<string, InvoiceEmailSyncSettings>(StringComparer.OrdinalIgnoreCase);
            var protectedBytes = File.ReadAllBytes(_settingsPath);
            var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, InvoiceEmailSyncSettings>>(clear)
                       ?? new Dictionary<string, InvoiceEmailSyncSettings>(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return new Dictionary<string, InvoiceEmailSyncSettings>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveAll(Dictionary<string, InvoiceEmailSyncSettings> settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(_settingsPath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static string NormalizeStoreKey(string value)
        => string.IsNullOrWhiteSpace(value) ? "DEFAULT" : value.Trim().ToUpperInvariant();

    private static string InvoiceKey(string? vendor, string? invoice)
        => $"{NormalizeVendor(vendor)}|{NormalizeIdentifier(invoice)}";

    private static string NormalizeVendor(string? value)
    {
        var normalized = NormalizeIdentifier(value);
        foreach (var suffix in new[] { "LIMITEDLIABILITYCOMPANY", "CORPORATION", "INCORPORATED", "COMPANY", "LLC", "CORP", "INC", "LTD", "CO" })
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                normalized = normalized[..^suffix.Length];
        }
        return normalized;
    }

    private static string NormalizeIdentifier(string? value)
        => new((value ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string MakeSafeFileName(string value)
    {
        var safe = value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        return safe.Length <= 120 ? safe : safe[..120];
    }

    private static void MoveToReview(string source, string reviewFolder)
    {
        var target = Path.Combine(reviewFolder, Path.GetFileName(source));
        if (File.Exists(target))
            target = Path.Combine(reviewFolder,
                $"{Path.GetFileNameWithoutExtension(source)}_{Guid.NewGuid():N}{Path.GetExtension(source)}");
        File.Move(source, target);
    }
}
