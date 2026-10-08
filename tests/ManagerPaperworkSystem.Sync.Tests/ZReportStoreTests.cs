using ManagerPaperworkSystem.WinForms;
using Xunit;
namespace ManagerPaperworkSystem.Sync.Tests;
public class ZReportStoreTests
{
    private const string Expected = "GALAXY SMOKE SHOP (ELGIN, IL - 60123)";
    private static string Receipt(string name, string city = "ELGIN, IL", string separator = "\n") =>
        string.Join(separator, name, "20 S STATE ST", city, "Z-Report", "========", "Register Number: 2", "Batch: 1548", "User: admin", "Start Date: 09/30/2026 08:38:23");

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("                 ")]
    public void CorrectPdfAndRenderedHeadersAccepted(string separator) =>
        PortalStoreIsolationPolicy.ValidateZReportStore(Receipt("GALAXY SMOKE SHOP", separator: separator), Expected);

    [Theory]
    [InlineData("1 (POS1)", "\n")]
    [InlineData("2 (POSS)", "\r\n")]
    [InlineData("2 (POSS)", "                 ")]
    [InlineData("2 (Front Register)", "\n")]
    public void NamedRegisterStillVerifiesReceiptStore(string register, string separator)
    {
        var receipt = Receipt("GALAXY SMOKE SHOP", "HANOVER PARK, IL", separator)
            .Replace("Register Number: 2", "Register Number: " + register);
        PortalStoreIsolationPolicy.ValidateZReportStore(receipt, "GALAXY SMOKE SHOP (HANOVER PARK, IL)");
        Assert.Throws<InvalidOperationException>(() => PortalStoreIsolationPolicy.ValidateZReportStore(receipt, Expected));
    }

    [Fact]
    public void NamedRegisterOnWrongStorePageCannotBeSkipped()
    {
        var other = Receipt("ELGIN SMOKE SHOP").Replace("Register Number: 2", "Register Number: 2 (POS1)");
        Assert.Throws<InvalidOperationException>(() => PortalStoreIsolationPolicy.ValidateZReportStore(
            Receipt("GALAXY SMOKE SHOP") + "\n" + other, Expected));
    }

    [Theory]
    [InlineData("ELGIN SMOKE SHOP", "ELGIN, IL")]
    [InlineData("GALAXY SMOKE SHOP", "CARPENTERSVILLE, IL")]
    [InlineData("GALAXY", "ELGIN, IL")]
    public void WrongNameOrCityRejected(string name, string city) => Assert.Throws<InvalidOperationException>(() =>
        PortalStoreIsolationPolicy.ValidateZReportStore(Receipt(name, city), Expected));

    [Theory]
    [InlineData("")]
    [InlineData("GALAXY SMOKE SHOP\nZ-Report\nRegister Number: 2\nBatch: 1548")]
    [InlineData("GALAXY SMOKE SHOP\n20 S STATE ST\nELGIN, IL\nZ-Report\nError loading report")]
    [InlineData("Z-Report\nRegister Number: 2\nBatch: 1548\nGALAXY SMOKE SHOP\n20 S STATE ST\nELGIN, IL")]
    public void MissingOrUnverifiableHeaderRejected(string source) => Assert.Throws<InvalidOperationException>(() =>
        PortalStoreIsolationPolicy.ValidateZReportStore(source, Expected));

    [Fact]
    public void ViewerToolbarBeforeValidReceiptAccepted() =>
        PortalStoreIsolationPolicy.ValidateZReportStore("Print Z Report\n" + Receipt("GALAXY SMOKE SHOP"), Expected);

    [Fact]
    public void ViewerStoreLabelCannotOverrideReceipt() => Assert.Throws<InvalidOperationException>(() =>
        PortalStoreIsolationPolicy.ValidateZReportStore("GALAXY SMOKE SHOP\nPrint Z Report\n" + Receipt("ELGIN SMOKE SHOP"), Expected));

    [Fact]
    public void MixedStorePagesCannotPassUsingFirstHeader() => Assert.Throws<InvalidOperationException>(() =>
        PortalStoreIsolationPolicy.ValidateZReportStore(Receipt("GALAXY SMOKE SHOP") + "\n" + Receipt("ELGIN SMOKE SHOP"), Expected));

    [Fact]
    public void MultiPageSameStoreAndUnitAddressAccepted() =>
        PortalStoreIsolationPolicy.ValidateZReportStore(Receipt("GALAXY SMOKE SHOP").Replace("ELGIN, IL", "UNIT 25\nELGIN, IL") + "\n" + Receipt("GALAXY SMOKE SHOP"), Expected);
}
