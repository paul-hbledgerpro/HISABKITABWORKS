using System.Globalization;
using ClosedXML.Excel;
using ManagerPaperworkSystem.UI.Services;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public class ZReportDateTests
{
    public static IEnumerable<object[]> Dates()
    {
        foreach (var culture in new[] { "en-US", "en-GB", "en-IN", "fr-FR", "ar-SA" })
        foreach (var date in new[] { new DateOnly(2026, 9, 30), new(2026, 10, 1), new(2026, 10, 2), new(2026, 1, 10), new(2026, 12, 11), new(2026, 12, 31) })
            yield return [culture, date];
    }

    [Theory]
    [MemberData(nameof(Dates))]
    public void PortalDatesDoNotDependOnWindowsCulture(string culture, DateOnly expected)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var source = $"GALAXY SMOKE SHOP\n20 S STATE ST\nELGIN, IL\nZ-Report\nRegister Number: 2\nBatch: 1550\nUser: admin\nStart Date: {expected.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)} 08:00:00\nEnd Date: {expected.AddDays(1).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)} 01:00:00\nNet Sales: 10.00\nCASH (1) 10.00";
            var report = new PosReportImportService().ImportRenderedZReport(source);
            Assert.Equal(expected, report.ReportDate);
            Assert.Equal("1550", report.ShiftOrBatch);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-IN")]
    [InlineData("fr-FR")]
    public void NamedDatesAndEndDateFallbackRemainSupported(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var importer = new PosReportImportService();
            Assert.Equal(new DateOnly(2026, 10, 1), importer.ImportRenderedZReport(
                "Z-Report\nBatch: 1550\nStart Date: Oct 1, 2026 08:00 AM     Net Sales: 10.00").ReportDate);
            Assert.Equal(new DateOnly(2026, 10, 2), importer.ImportRenderedZReport(
                "Z-Report\nBatch: 1550\nEnd Date: 10/02/2026 01:00 AM").ReportDate);
            Assert.Null(importer.ImportRenderedZReport(
                "Z-Report\nBatch: 1550\nStart Date: 13/01/2026").ReportDate);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("en-IN")]
    public void WorkbookUsesSamePortalDateConvention(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        var file = Path.Combine(Path.GetTempPath(), $"HK-ZDate-{Guid.NewGuid():N}.xlsx");
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Z Report");
                sheet.Cell(1, 1).Value = "Z-Report Batch: 1550 User: admin";
                sheet.Cell(2, 1).Value = "Start Date: 10/01/2026 08:00:00 End Date: 10/02/2026 01:00:00";
                book.SaveAs(file);
            }
            Assert.Equal(new DateOnly(2026, 10, 1), new PosReportImportService().Import(file).ReportDate);
        }
        finally { CultureInfo.CurrentCulture = before; File.Delete(file); }
    }
}
