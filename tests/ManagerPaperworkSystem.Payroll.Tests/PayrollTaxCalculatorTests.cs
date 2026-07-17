using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Payroll;
using Xunit;

namespace ManagerPaperworkSystem.Payroll.Tests;

public sealed class PayrollTaxCalculatorTests
{
    private static readonly PayrollTaxRuleSet Rules = LoadRules();

    [Fact]
    public void PackageContainsEveryStateAndDistrictOfColumbia()
    {
        Assert.Equal(51, Rules.States.Count);
        Assert.Equal(51, Rules.States.Select(rule => rule.StateCode).Distinct().Count());
        Assert.Contains(Rules.States, rule => rule.StateCode == "DC");
    }

    [Fact]
    public void IllinoisUsesVerifiedFlatRateRule()
    {
        var employee = Employee("IL");
        var result = PayrollTaxCalculator.Calculate(
            employee,
            regularHours: 40,
            overtimeHours: 0,
            holidayHours: 0,
            bonusPay: 0,
            cashAdvance: 0,
            otherDeduction: 0,
            priorGrossYtd: 0,
            payDate: new DateOnly(2026, 7, 17),
            Rules);

        Assert.Equal(2000m, result.GrossPay);
        Assert.Equal(99m, result.StateWithholding);
        Assert.Equal("IL-IL700T-2026", result.StateRuleId);
    }

    [Theory]
    [InlineData("AK")]
    [InlineData("FL")]
    [InlineData("NV")]
    [InlineData("NH")]
    [InlineData("SD")]
    [InlineData("TN")]
    [InlineData("TX")]
    [InlineData("WA")]
    public void VerifiedNoIncomeTaxStatesWithholdZero(string state)
    {
        var result = PayrollTaxCalculator.Calculate(
            Employee(state),
            40,
            0,
            0,
            0,
            0,
            0,
            0,
            new DateOnly(2026, 7, 17),
            Rules);
        Assert.Equal(0m, result.StateWithholding);
    }

    [Fact]
    public void PendingStateFailsClosed()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            PayrollTaxCalculator.Calculate(
                Employee("CA"),
                40,
                0,
                0,
                0,
                0,
                0,
                0,
                new DateOnly(2026, 7, 17),
                Rules));
        Assert.Contains("not been verified", exception.Message);
    }

    [Fact]
    public void WrongTaxYearFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PayrollTaxCalculator.Calculate(
                Employee("IL"),
                40,
                0,
                0,
                0,
                0,
                0,
                0,
                new DateOnly(2027, 1, 2),
                Rules));
    }

    [Fact]
    public void SocialSecurityStopsAtAnnualWageBase()
    {
        var result = PayrollTaxCalculator.Calculate(
            Employee("TX"),
            40,
            0,
            0,
            0,
            0,
            0,
            priorGrossYtd: 184000m,
            new DateOnly(2026, 7, 17),
            Rules);
        Assert.Equal(31m, result.SocialSecurity);
    }

    private static Employee Employee(string workState) => new()
    {
        FirstName = "Test",
        LastName = "Employee",
        PayRate = 50m,
        PayType = EmployeePayType.Hourly,
        PayFrequency = PayFrequency.Weekly,
        WorkState = workState,
        ResidenceState = workState,
        FederalFilingStatus = FederalFilingStatus.SingleOrMarriedFilingSeparately,
        StateFilingStatus = "Single"
    };

    private static PayrollTaxRuleSet LoadRules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TaxRules", "us-payroll-2026.json");
        return JsonSerializer.Deserialize<PayrollTaxRuleSet>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Test tax rules could not be loaded.");
    }
}
