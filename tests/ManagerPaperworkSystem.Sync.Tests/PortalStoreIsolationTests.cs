using ManagerPaperworkSystem.WinForms;
using Xunit;

namespace ManagerPaperworkSystem.Sync.Tests;

public class PortalStoreIsolationTests
{
    private const string Elgin = "GALAXY SMOKE SHOP (ELGIN, IL)";

    [Fact]
    public void ExactLocationWinsOverOtherStoreAndOtherCity() => Assert.Equal(2,
        PortalStoreIsolationPolicy.SelectExactStore(Elgin,
            ["ELGIN SMOKE SHOP", "GALAXY SMOKE SHOP (CARPENTERSVILLE, IL)", Elgin]));

    [Theory]
    [InlineData("GALAXY SMOKE SHOP")]
    [InlineData("ELGIN")]
    [InlineData("")]
    [InlineData("GALAXY SMOKE SHOP (HANOVER, IL)")]
    public void PartialOrMissingStoreCannotSelect(string configured) =>
        Assert.Throws<InvalidOperationException>(() => PortalStoreIsolationPolicy.SelectExactStore(
            configured, [Elgin, "GALAXY SMOKE SHOP (CARPENTERSVILLE, IL)"]));

    [Fact]
    public void DuplicateExactNamesAreAmbiguous() =>
        Assert.Throws<InvalidOperationException>(() => PortalStoreIsolationPolicy.SelectExactStore(
            Elgin, [Elgin, "Galaxy Smoke Shop (Elgin IL)"]));

    [Theory]
    [InlineData("GALAXY SMOKE SHOP")]
    [InlineData("Galaxy Smoke Shop (Elgin, IL)")]
    [InlineData("  galaxy smoke shop  ")]
    public void VerifiedPortalAllowsKnownHeaderFormat(string header) =>
        PortalStoreIsolationPolicy.ValidateSummaryStore(header, Elgin);

    [Theory]
    [InlineData("ELGIN SMOKE SHOP")]
    [InlineData("GALAXY SMOKE SHOP (CARPENTERSVILLE, IL)")]
    [InlineData("GALAXY")]
    [InlineData("")]
    public void WrongOrMissingSummaryStoreCannotImport(string header) =>
        Assert.Throws<InvalidOperationException>(() => PortalStoreIsolationPolicy.ValidateSummaryStore(header, Elgin));

    [Fact]
    public void BlankConfigurationCannotApproveHeader() =>
        Assert.Throws<InvalidOperationException>(() => PortalStoreIsolationPolicy.ValidateSummaryStore("GALAXY", ""));

    [Theory]
    [InlineData("HBStoreLedger_GALAXY ELGIN", "HBStoreLedger_ELGIN SMOKE SHOP", false)]
    [InlineData("HBStoreLedger_GALAXY ELGIN", "hbstoreledger_galaxy elgin", true)]
    [InlineData("", "HBStoreLedger_GALAXY ELGIN", true)]
    public void StoredDatabaseCannotBeOverriddenByBusinessId(string configured, string licensed, bool allowed) =>
        Assert.Equal(allowed, PortalStoreIsolationPolicy.DatabaseMatches(configured, licensed));
}
