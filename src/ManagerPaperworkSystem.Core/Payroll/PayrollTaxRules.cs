using System.Text.Json.Serialization;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.Core.Payroll;

public enum StateWithholdingMethod
{
    Unavailable = 0,
    NoWithholding = 1,
    FlatPercentage = 2,
    AnnualizedBrackets = 3,
    PercentageOfFederalWithholding = 4
}

public sealed class PayrollTaxRuleSet
{
    public int SchemaVersion { get; set; } = 1;
    public string RuleSetId { get; set; } = "";
    public string Version { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }
    public DateTime VerifiedUtc { get; set; }
    public FederalWithholdingRule Federal { get; set; } = new();
    public List<StateWithholdingRule> States { get; set; } = new();
    public List<TaxRuleSource> Sources { get; set; } = new();

    [JsonIgnore]
    public string SourceSummary => string.Join(
        " | ",
        Sources.Select(source => $"{source.Authority}: {source.Url}").Distinct(StringComparer.OrdinalIgnoreCase));
}

public sealed class FederalWithholdingRule
{
    public int TaxYear { get; set; }
    public decimal SocialSecurityRate { get; set; }
    public decimal SocialSecurityWageBase { get; set; }
    public decimal MedicareRate { get; set; }
    public decimal AdditionalMedicareRate { get; set; }
    public decimal AdditionalMedicareThreshold { get; set; }
    public Dictionary<string, decimal> StandardAdjustments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<TaxBracketRule>> StandardBrackets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<TaxBracketRule>> MultipleJobsBrackets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SourceUrl { get; set; } = "";
}

public sealed class StateWithholdingRule
{
    public string StateCode { get; set; } = "";
    public string StateName { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Verified { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }
    public StateWithholdingMethod Method { get; set; }
    public decimal Rate { get; set; }
    public decimal PrimaryAllowance { get; set; }
    public decimal AdditionalAllowance { get; set; }
    public Dictionary<string, decimal> StandardDeductions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<TaxBracketRule>> Brackets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SourceAuthority { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class TaxBracketRule
{
    public decimal Floor { get; set; }
    public decimal BaseTax { get; set; }
    public decimal Rate { get; set; }
}

public sealed class TaxRuleSource
{
    public string Authority { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime VerifiedUtc { get; set; }
}

public sealed record PayrollCalculation(
    decimal RegularPay,
    decimal OvertimePay,
    decimal HolidayPay,
    decimal BonusPay,
    decimal GrossPay,
    decimal FederalWithholding,
    decimal SocialSecurity,
    decimal Medicare,
    decimal StateWithholding,
    decimal NetPay,
    string StateRuleId,
    string StateRuleVersion);

public static class PayrollTaxCalculator
{
    public static PayrollCalculation Calculate(
        Employee employee,
        decimal regularHours,
        decimal overtimeHours,
        decimal holidayHours,
        decimal bonusPay,
        decimal cashAdvance,
        decimal otherDeduction,
        decimal priorGrossYtd,
        DateOnly payDate,
        PayrollTaxRuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ValidateRuleSet(ruleSet, payDate);

        var periods = PeriodsPerYear(employee.PayFrequency);
        decimal regularPay;
        decimal overtimePay;
        decimal holidayPay;
        if (employee.PayType == EmployeePayType.Salary)
        {
            regularPay = employee.PayRate / periods;
            overtimePay = employee.IsOvertimeEligible ? overtimeHours * HourlyEquivalent(employee) * 1.5m : 0;
            holidayPay = holidayHours * HourlyEquivalent(employee) * employee.HolidayMultiplier;
        }
        else
        {
            regularPay = regularHours * employee.PayRate;
            overtimePay = overtimeHours * employee.PayRate * (employee.IsOvertimeEligible ? 1.5m : 1m);
            holidayPay = holidayHours * employee.PayRate * employee.HolidayMultiplier;
        }

        var gross = Money(regularPay + overtimePay + holidayPay + Math.Max(0, bonusPay));
        var federal = Federal(employee, gross, periods, ruleSet.Federal);

        var socialSecurityTaxable = Math.Max(
            0,
            Math.Min(gross, ruleSet.Federal.SocialSecurityWageBase - Math.Max(0, priorGrossYtd)));
        var socialSecurity = Money(socialSecurityTaxable * ruleSet.Federal.SocialSecurityRate);
        var medicare = Money(gross * ruleSet.Federal.MedicareRate);
        if (priorGrossYtd + gross > ruleSet.Federal.AdditionalMedicareThreshold)
        {
            var additionalTaxable = Math.Min(
                gross,
                Math.Max(0, priorGrossYtd + gross - ruleSet.Federal.AdditionalMedicareThreshold));
            medicare += Money(additionalTaxable * ruleSet.Federal.AdditionalMedicareRate);
        }

        var stateRule = GetStateRule(ruleSet, employee.WorkState, payDate);
        var state = State(employee, gross, federal, periods, stateRule);
        var deductions = Math.Max(0, cashAdvance) + Math.Max(0, otherDeduction);
        var net = Money(Math.Max(0, gross - federal - socialSecurity - medicare - state - deductions));

        return new PayrollCalculation(
            Money(regularPay),
            Money(overtimePay),
            Money(holidayPay),
            Money(Math.Max(0, bonusPay)),
            gross,
            federal,
            socialSecurity,
            medicare,
            state,
            net,
            stateRule.RuleId,
            stateRule.Version);
    }

    public static int PeriodsPerYear(PayFrequency frequency) => frequency switch
    {
        PayFrequency.Weekly => 52,
        PayFrequency.Biweekly => 26,
        PayFrequency.Semimonthly => 24,
        PayFrequency.Monthly => 12,
        _ => throw new InvalidOperationException($"Unsupported pay frequency: {frequency}.")
    };

    public static StateWithholdingRule GetStateRule(
        PayrollTaxRuleSet ruleSet,
        string? stateCode,
        DateOnly payDate)
    {
        var normalized = NormalizeStateCode(stateCode);
        var rule = ruleSet.States.FirstOrDefault(candidate =>
            string.Equals(candidate.StateCode, normalized, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            throw new NotSupportedException($"No state withholding definition exists for {normalized}.");
        if (!rule.Verified || rule.Method == StateWithholdingMethod.Unavailable)
            throw new NotSupportedException(
                $"{rule.StateName} ({rule.StateCode}) withholding has not been verified for {payDate.Year}. " +
                "Install a signed tax-rule update before calculating payroll.");
        if (payDate < rule.EffectiveFrom || payDate > rule.EffectiveTo)
            throw new NotSupportedException(
                $"{rule.StateName} withholding rule {rule.Version} is not effective on {payDate:MM/dd/yyyy}.");
        return rule;
    }

    public static string NormalizeStateCode(string? stateCode)
    {
        var normalized = (stateCode ?? "").Trim().ToUpperInvariant();
        if (normalized.Length != 2 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new InvalidOperationException("A valid two-letter work-state code is required for payroll.");
        return normalized;
    }

    private static void ValidateRuleSet(PayrollTaxRuleSet ruleSet, DateOnly payDate)
    {
        if (ruleSet.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported payroll tax-rule schema {ruleSet.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(ruleSet.RuleSetId) || string.IsNullOrWhiteSpace(ruleSet.Version))
            throw new InvalidOperationException("The payroll tax-rule package is missing its identity or version.");
        if (payDate < ruleSet.EffectiveFrom || payDate > ruleSet.EffectiveTo)
            throw new InvalidOperationException(
                $"Tax-rule package {ruleSet.Version} is not effective on {payDate:MM/dd/yyyy}.");
        if (ruleSet.Federal.TaxYear != payDate.Year)
            throw new InvalidOperationException(
                $"Federal tax rules are for {ruleSet.Federal.TaxYear}, not {payDate.Year}.");
    }

    private static decimal Federal(Employee employee, decimal wages, int periods, FederalWithholdingRule rule)
    {
        if (employee.FederalExempt)
            return 0;

        var filingKey = FederalFilingKey(employee.FederalFilingStatus);
        var adjustment = employee.FederalMultipleJobs
            ? 0
            : GetValue(rule.StandardAdjustments, filingKey);
        var adjustedAnnual = Math.Max(
            0,
            wages * periods + employee.FederalOtherIncome - employee.FederalDeductions - adjustment);
        var tables = employee.FederalMultipleJobs ? rule.MultipleJobsBrackets : rule.StandardBrackets;
        if (!tables.TryGetValue(filingKey, out var brackets) || brackets.Count == 0)
            throw new InvalidOperationException($"Federal bracket table '{filingKey}' is missing.");

        var annualTax = ApplyBrackets(adjustedAnnual, brackets);
        var periodTax =
            Math.Max(0, annualTax / periods - employee.FederalDependentsCredit / periods) +
            employee.FederalExtraWithholding;
        return Money(periodTax);
    }

    private static decimal State(
        Employee employee,
        decimal wages,
        decimal federalWithholding,
        int periods,
        StateWithholdingRule rule)
    {
        if (employee.StateExempt || rule.Method == StateWithholdingMethod.NoWithholding)
            return 0;

        var filingKey = NormalizeStateFilingStatus(employee.StateFilingStatus);
        var allowances = employee.StateAllowances;
        var additionalAllowances = employee.StateAdditionalAllowances;
        var extra = employee.StateExtraWithholding;

        // Existing Illinois records remain valid after the generalized state fields are added.
        if (string.Equals(rule.StateCode, "IL", StringComparison.OrdinalIgnoreCase))
        {
            if (allowances == 0 && employee.IllinoisLine1Allowances != 0)
                allowances = employee.IllinoisLine1Allowances;
            if (additionalAllowances == 0 && employee.IllinoisLine2Allowances != 0)
                additionalAllowances = employee.IllinoisLine2Allowances;
            if (extra == 0 && employee.IllinoisExtraWithholding != 0)
                extra = employee.IllinoisExtraWithholding;
        }

        decimal periodTax;
        switch (rule.Method)
        {
            case StateWithholdingMethod.FlatPercentage:
            {
                var annualWages = wages * periods;
                var standardDeduction = GetValue(rule.StandardDeductions, filingKey);
                var annualExemptions =
                    Math.Max(0, allowances) * rule.PrimaryAllowance +
                    Math.Max(0, additionalAllowances) * rule.AdditionalAllowance;
                var annualTaxable = Math.Max(
                    0,
                    annualWages - standardDeduction - annualExemptions - Math.Max(0, employee.StateDeductions));
                var annualTax = Math.Max(0, annualTaxable * rule.Rate - Math.Max(0, employee.StateCredits));
                periodTax = annualTax / periods;
                break;
            }
            case StateWithholdingMethod.AnnualizedBrackets:
            {
                if (!rule.Brackets.TryGetValue(filingKey, out var brackets) || brackets.Count == 0)
                    throw new InvalidOperationException(
                        $"{rule.StateName} bracket table '{filingKey}' is missing.");
                var annualWages = wages * periods;
                var standardDeduction = GetValue(rule.StandardDeductions, filingKey);
                var annualExemptions =
                    Math.Max(0, allowances) * rule.PrimaryAllowance +
                    Math.Max(0, additionalAllowances) * rule.AdditionalAllowance;
                var annualTaxable = Math.Max(
                    0,
                    annualWages - standardDeduction - annualExemptions - Math.Max(0, employee.StateDeductions));
                periodTax =
                    Math.Max(0, ApplyBrackets(annualTaxable, brackets) - Math.Max(0, employee.StateCredits)) /
                    periods;
                break;
            }
            case StateWithholdingMethod.PercentageOfFederalWithholding:
                periodTax = federalWithholding * rule.Rate;
                break;
            default:
                throw new NotSupportedException(
                    $"{rule.StateName} withholding method is not available in this rule engine.");
        }

        return Money(Math.Max(0, periodTax + Math.Max(0, extra)));
    }

    private static decimal ApplyBrackets(decimal annualTaxable, IReadOnlyCollection<TaxBracketRule> brackets)
    {
        var bracket = brackets
            .OrderBy(candidate => candidate.Floor)
            .Last(candidate => annualTaxable >= candidate.Floor);
        return bracket.BaseTax + (annualTaxable - bracket.Floor) * bracket.Rate;
    }

    private static decimal GetValue(IReadOnlyDictionary<string, decimal> values, string key)
    {
        if (values.TryGetValue(key, out var value))
            return value;
        if (values.TryGetValue("All", out value))
            return value;
        return 0;
    }

    private static string FederalFilingKey(FederalFilingStatus status) => status switch
    {
        FederalFilingStatus.MarriedFilingJointly => "Married",
        FederalFilingStatus.HeadOfHousehold => "HeadOfHousehold",
        _ => "Single"
    };

    private static string NormalizeStateFilingStatus(string? status)
    {
        var normalized = (status ?? "").Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        if (normalized.Equals("MarriedFilingJointly", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Married", StringComparison.OrdinalIgnoreCase))
            return "Married";
        if (normalized.Equals("HeadOfHousehold", StringComparison.OrdinalIgnoreCase))
            return "HeadOfHousehold";
        return "Single";
    }

    private static decimal HourlyEquivalent(Employee employee)
        => employee.PayRate <= 0 ? 0 : employee.PayRate / 2_080m;

    private static decimal Money(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
