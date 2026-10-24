using System.Text;
using Tesseract;
using UglyToad.PdfPig;

namespace ManagerPaperworkSystem.WinForms;

internal static class ScannedInvoiceReader
{
    // Offline OCR. Extract embedded scan images without sending invoices to a third party.
    // Unsupported tiled/rotated scans remain in Needs Review, never auto-posted.
    public static string Read(string path, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages > 25) throw new InvalidOperationException("Split scans longer than 25 pages for review.");
        using var engine = new TesseractEngine(Path.Combine(AppContext.BaseDirectory, "tessdata"), "eng", EngineMode.LstmOnly);
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var images = page.GetImages().Where(x => x.WidthInSamples >= 600 && x.HeightInSamples >= 600).ToList();
            if (images.Count != 1) throw new InvalidOperationException("The scan requires manual review.");
            var image = images[0];
            if ((long)image.WidthInSamples * image.HeightInSamples > 30_000_000) throw new InvalidOperationException("Scan resolution is too large.");
            byte[] bytes;
            if (!image.TryGetPng(out bytes!)) bytes = image.RawBytes.ToArray();
            using var pix = Pix.LoadFromMemory(bytes);
            using var result = engine.Process(pix, PageSegMode.Auto);
            text.AppendLine(result.GetText());
        }
        return text.ToString();
    }
}
