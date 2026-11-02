using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public static class ShiftShortageService
{
    public static string[] ParseRecipients(string value)
    {
        var addresses = value.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (addresses.Length is < 1 or > 10) throw new InvalidOperationException("Enter between 1 and 10 recipient email addresses.");
        foreach (var address in addresses)
            if (address.Length > 254 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || !address.Contains('.') || address.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("Enter email addresses only, separated by commas or new lines.");
        return addresses.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public static async Task SaveSettingsAsync(AppDbContext db, ShiftShortageSettings values, bool canManage)
    {
        if (!canManage) throw new UnauthorizedAccessException("Only an owner or an unlocked developer can change shortage alerts.");
        if (values.StoreId <= 0 || string.IsNullOrWhiteSpace(values.StoreGuid)) throw new InvalidOperationException("Select a licensed store first.");
        if (values.Threshold < 0 || values.Threshold > 99999999 || decimal.Round(values.Threshold, 2) != values.Threshold) throw new InvalidOperationException("Enter a valid shortage limit with up to two decimal places.");
        var recipients = JsonSerializer.Deserialize<string[]>(values.RecipientsJson) ?? [];
        if (values.Enabled || recipients.Length > 0) recipients = ParseRecipients(string.Join(";", recipients));
        values.RecipientsJson = JsonSerializer.Serialize(recipients);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var existing = await db.ShiftShortageSettings.SingleOrDefaultAsync(x => x.StoreId == values.StoreId);
        var changed = existing is null || existing.Enabled != values.Enabled || existing.Threshold != values.Threshold ||
            existing.StoreGuid != values.StoreGuid || existing.RecipientsJson != values.RecipientsJson;
        if (existing is null) db.ShiftShortageSettings.Add(values);
        else db.Entry(existing).CurrentValues.SetValues(values);
        // Do not deliver a queued report to recipients the owner has removed.
        if (changed)
            await db.ShiftShortageEmails.Where(x => x.StoreId == values.StoreId && x.Status == "Queued")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled").SetProperty(x => x.Message, "Settings changed before delivery"));
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    // Called only by user-entry workflows, inside their transaction, never by raw Z-report imports.
    public static async Task QueueAsync(AppDbContext db, ShiftLogEntry shift, string enteredBy)
    {
        var settings = await db.ShiftShortageSettings.AsNoTracking().SingleOrDefaultAsync(x => x.StoreId == shift.StoreId);
        if (settings is null || !settings.Enabled || -shift.Variance <= settings.Threshold) return;
        var recipients = ParseRecipients(string.Join(";", JsonSerializer.Deserialize<string[]>(settings.RecipientsJson) ?? []));
        var report = new ShiftShortageReport(settings.StoreName, shift.ShiftNo, shift.Date, shift.Employee,
            shift.CashTotal, shift.CardTotal, shift.NetSales, shift.Tax, shift.CashDropReceived, shift.RegisterPayout,
            shift.PayoutPayee, shift.PayoutReason, -shift.Variance, settings.Threshold, enteredBy, DateTime.UtcNow, shift.IsCorrection);
        // A repeated save of identical values must not send another email. Store and shift IDs are part of the identity.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            settings.StoreGuid, shift.StoreId, shift.Id, Report = report with { EnteredUtc = default }
        }))));
        if (await db.ShiftShortageEmails.AnyAsync(x => x.Id == key)) return;
        db.ShiftShortageEmails.Add(new ShiftShortageEmail {
            Id = key, StoreId = shift.StoreId, StoreGuid = settings.StoreGuid,
            RecipientsJson = JsonSerializer.Serialize(recipients), ReportJson = JsonSerializer.Serialize(report),
            CreatedUtc = report.EnteredUtc, NextAttemptUtc = report.EnteredUtc
        });
    }
}
