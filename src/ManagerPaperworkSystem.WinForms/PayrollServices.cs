using System.Security.Cryptography;
using System.Text;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.WinForms;

internal static class PayrollSensitiveDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-PAYROLL-SENSITIVE-V1");

    public static byte[] Protect(byte[] clear)
        => ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] encrypted)
        => ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);

    public static byte[] ProtectText(string value)
    {
        var clear = Encoding.UTF8.GetBytes(value ?? "");
        try { return Protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static string UnprotectText(byte[] encrypted)
    {
        if (encrypted is null || encrypted.Length == 0) return "";
        var clear = Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}

internal sealed record PayrollCalculation(
    decimal RegularPay,
    decimal OvertimePay,
    decimal HolidayPay,
    decimal BonusPay,
    decimal GrossPay,
    decimal FederalWithholding,
    decimal SocialSecurity,
    decimal Medicare,
    decimal StateWithholding,
    decimal NetPay);

internal static class PayrollCalculator2026
{
    public const int TaxYear = 2026;
    private const decimal SocialSecurityRate = 0.062m;
    private const decimal SocialSecurityWageBase = 184_500m;
    private const decimal MedicareRate = 0.0145m;
    private const decimal AdditionalMedicareRate = 0.009m;
    private const decimal AdditionalMedicareThreshold = 200_000m;

    private sealed record Bracket(decimal Floor, decimal BaseTax, decimal Rate);

    private static readonly Bracket[] MarriedStandard =
    {
        new(0, 0, 0), new(19_300, 0, .10m), new(44_100, 2_480, .12m),
        new(120_100, 11_600, .22m), new(230_700, 35_932, .24m),
        new(422_850, 82_048, .32m), new(531_750, 116_896, .35m),
        new(788_000, 206_583.50m, .37m)
    };

    private static readonly Bracket[] MarriedMultipleJobs =
    {
        new(0, 0, 0), new(16_100, 0, .10m), new(28_500, 1_240, .12m),
        new(66_500, 5_800, .22m), new(121_800, 17_966, .24m),
        new(217_875, 41_024, .32m), new(272_325, 58_448, .35m),
        new(400_450, 103_291.75m, .37m)
    };

    private static readonly Bracket[] SingleStandard =
    {
        new(0, 0, 0), new(7_500, 0, .10m), new(19_900, 1_240, .12m),
        new(57_900, 5_800, .22m), new(113_200, 17_966, .24m),
        new(209_275, 41_024, .32m), new(263_725, 58_448, .35m),
        new(648_100, 192_979.25m, .37m)
    };

    private static readonly Bracket[] SingleMultipleJobs =
    {
        new(0, 0, 0), new(8_050, 0, .10m), new(14_250, 620, .12m),
        new(33_250, 2_900, .22m), new(60_900, 8_983, .24m),
        new(108_938, 20_512, .32m), new(136_163, 29_224, .35m),
        new(328_350, 96_489.63m, .37m)
    };

    private static readonly Bracket[] HeadStandard =
    {
        new(0, 0, 0), new(15_550, 0, .10m), new(33_250, 1_770, .12m),
        new(83_000, 7_740, .22m), new(121_250, 16_155, .24m),
        new(217_300, 39_207, .32m), new(271_750, 56_631, .35m),
        new(656_150, 191_171, .37m)
    };

    private static readonly Bracket[] HeadMultipleJobs =
    {
        new(0, 0, 0), new(12_075, 0, .10m), new(20_925, 885, .12m),
        new(45_800, 3_870, .22m), new(64_925, 8_077.50m, .24m),
        new(112_950, 19_603.50m, .32m), new(140_175, 28_315.50m, .35m),
        new(332_375, 95_585.50m, .37m)
    };

    public static PayrollCalculation Calculate(
        Employee employee,
        decimal regularHours,
        decimal overtimeHours,
        decimal holidayHours,
        decimal bonusPay,
        decimal cashAdvance,
        decimal otherDeduction,
        decimal priorGrossYtd)
    {
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
        var federal = Federal(employee, gross, periods);

        var socialSecurityTaxable = Math.Max(0, Math.Min(gross, SocialSecurityWageBase - priorGrossYtd));
        var socialSecurity = Money(socialSecurityTaxable * SocialSecurityRate);
        var medicare = Money(gross * MedicareRate);
        if (priorGrossYtd + gross > AdditionalMedicareThreshold)
        {
            var additionalTaxable = Math.Min(gross, priorGrossYtd + gross - AdditionalMedicareThreshold);
            medicare += Money(additionalTaxable * AdditionalMedicareRate);
        }

        if (!employee.WorkState.Equals("IL", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"State withholding for {employee.WorkState} is not configured. Add the official state tax rules before processing this employee.");
        var state = Illinois(employee, gross, periods);
        var deductions = Math.Max(0, cashAdvance) + Math.Max(0, otherDeduction);
        var net = Money(Math.Max(0, gross - federal - socialSecurity - medicare - state - deductions));
        return new PayrollCalculation(Money(regularPay), Money(overtimePay), Money(holidayPay), Money(Math.Max(0, bonusPay)), gross, federal, socialSecurity, medicare, state, net);
    }

    public static int PeriodsPerYear(PayFrequency frequency) => frequency switch
    {
        PayFrequency.Weekly => 52,
        PayFrequency.Biweekly => 26,
        PayFrequency.Semimonthly => 24,
        PayFrequency.Monthly => 12,
        _ => 26
    };

    private static decimal Federal(Employee e, decimal wages, int periods)
    {
        if (e.FederalExempt) return 0;
        var standardAdjustment = e.FederalMultipleJobs
            ? 0m
            : e.FederalFilingStatus == FederalFilingStatus.MarriedFilingJointly ? 12_900m : 8_600m;
        var adjustedAnnual = Math.Max(0,
            wages * periods + e.FederalOtherIncome - e.FederalDeductions - standardAdjustment);
        var brackets = e.FederalFilingStatus switch
        {
            FederalFilingStatus.MarriedFilingJointly => e.FederalMultipleJobs ? MarriedMultipleJobs : MarriedStandard,
            FederalFilingStatus.HeadOfHousehold => e.FederalMultipleJobs ? HeadMultipleJobs : HeadStandard,
            _ => e.FederalMultipleJobs ? SingleMultipleJobs : SingleStandard
        };
        var bracket = brackets.Last(x => adjustedAnnual >= x.Floor);
        var annualTax = bracket.BaseTax + (adjustedAnnual - bracket.Floor) * bracket.Rate;
        var periodTax = Math.Max(0, annualTax / periods - e.FederalDependentsCredit / periods) + e.FederalExtraWithholding;
        return Money(periodTax);
    }

    private static decimal Illinois(Employee e, decimal wages, int periods)
    {
        var annualExemptions = Math.Max(0, e.IllinoisLine1Allowances) * 2_925m
                               + Math.Max(0, e.IllinoisLine2Allowances) * 1_000m;
        var taxable = Math.Max(0, wages - annualExemptions / periods);
        return Money(taxable * .0495m + Math.Max(0, e.IllinoisExtraWithholding));
    }

    private static decimal HourlyEquivalent(Employee employee)
        => employee.PayRate <= 0 ? 0 : employee.PayRate / 2_080m;

    private static decimal Money(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
