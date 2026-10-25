using ManagerPaperworkSystem.WinForms;
using MimeKit;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class InvoiceEmailSearchTests
{
    [Fact]
    public void MimeTypedPdfWithoutExtensionAndForwardedPdfAreDiscovered()
    {
        MimePart Pdf(string name) => new("application", "pdf") { FileName = name, Content = new MimeContent(new MemoryStream("%PDF-fixture"u8.ToArray())) };
        var outer = Pdf("attachment"); var inner = Pdf("AK-INVOICE.PDF");
        var body = new Multipart("mixed") { new TextPart("plain") { Text = "Invoice attached" }, outer,
            new MessagePart { Message = new MimeMessage { Body = new Multipart("mixed") { inner } } },
            new MimePart("image", "png") { FileName = "logo.png", Content = new MimeContent(new MemoryStream([1,2,3])) } };
        Assert.Equal(new[] { outer, inner }, InvoiceEmailSearchPolicy.PdfParts(body).ToArray());
        Assert.Equal("attachment.pdf", InvoiceEmailSearchPolicy.PdfFileName(outer.FileName));
        Assert.Equal("AK-INVOICE.PDF", InvoiceEmailSearchPolicy.PdfFileName(inner.FileName));
    }
    [Fact]
    public void QueryHasBoundedDatesWithoutPdfFilenameRestriction()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var query = InvoiceEmailSearchPolicy.GmailQuery(now.AddDays(-1), null, now);
        Assert.Contains("has:attachment", query); Assert.DoesNotContain("filename:", query);
        Assert.Contains("after:" + new DateTimeOffset(now.AddDays(-3)).ToUnixTimeSeconds(), query);
        Assert.Contains("before:" + new DateTimeOffset(now.AddSeconds(1)).ToUnixTimeSeconds(), query);
    }
}
