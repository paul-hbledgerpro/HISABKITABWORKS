using MimeKit;

namespace ManagerPaperworkSystem.WinForms;

internal static class InvoiceEmailSearchPolicy
{
    public static string GmailQuery(DateTime? lastSuccessUtc, DateOnly? month, DateTime startedUtc)
    {
        var start = month.HasValue
            ? new DateOnly(month.Value.Year, month.Value.Month, 1).AddDays(-7).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime()
            : (lastSuccessUtc ?? startedUtc.AddDays(-45)).AddDays(-2);
        var end = month.HasValue
            ? new DateOnly(month.Value.Year, month.Value.Month, 1).AddMonths(1).AddDays(14).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime()
            : startedUtc.AddSeconds(1);
        // Filename searches miss valid PDF MIME parts with no .pdf filename (and forwarded .eml PDFs).
        // Fetch attachment messages, then accept PDF parts locally. Spam/trash remain excluded.
        return $"has:attachment after:{new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)).ToUnixTimeSeconds()} before:{new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)).ToUnixTimeSeconds()}";
    }

    public static IEnumerable<MimePart> PdfParts(MimeEntity? entity)
    {
        if (entity is Multipart multipart)
        {
            foreach (var child in multipart)
            foreach (var pdf in PdfParts(child)) yield return pdf;
        }
        else if (entity is MessagePart nested)
        {
            foreach (var pdf in PdfParts(nested.Message?.Body)) yield return pdf;
        }
        else if (entity is MimePart part && part.Content is not null
            && ((part.FileName ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                || part.ContentType.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)))
            yield return part;
    }

    public static string PdfFileName(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "invoice.pdf" : name;
        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
    }
}
