using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManagerPaperworkSystem.Core.Payroll;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record TaxRulePackageSnapshot(
    PayrollTaxRuleSet RuleSet,
    string Sha256,
    string PackagePath,
    bool IsSignedUpdate);

internal sealed record TaxRuleUpdateResult(
    bool Checked,
    bool Installed,
    string Message,
    TaxRulePackageSnapshot Current);

internal static class TaxRulePackageService
{
    private const string GitHubLatestReleaseApi =
        "https://api.github.com/repos/paul-hbledgerpro/HISABKITABWORKS/releases/latest";
    private const string AssetPrefix = "HISAB_KITAB_TaxRules_";
    private const string BundledFileName = "us-payroll-2026.json";
    private const string InstalledFileName = "active.hktax";
    private const string CheckStateFileName = "tax-rule-update-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string InstalledDirectory =>
        Path.Combine(AppBootstrap.AppDataPath, "TaxRules");

    public static string InstalledPackagePath =>
        Path.Combine(InstalledDirectory, InstalledFileName);

    public static TaxRulePackageSnapshot LoadForDate(DateOnly payDate)
    {
        var selected = LoadAvailablePackages()
            .Where(package =>
                payDate >= package.RuleSet.EffectiveFrom &&
                payDate <= package.RuleSet.EffectiveTo &&
                package.RuleSet.Federal.TaxYear == payDate.Year)
            .OrderByDescending(package => ParseVersion(package.RuleSet.Version))
            .ThenByDescending(package => package.RuleSet.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selected is null)
            throw new InvalidOperationException(
                $"No verified payroll tax package is installed for {payDate:MM/dd/yyyy}. " +
                "Use CHECK TAX UPDATES before calculating payroll.");

        return selected;
    }

    public static TaxRulePackageSnapshot LoadLatestAvailable()
    {
        return LoadAvailablePackages()
            .OrderByDescending(package => ParseVersion(package.RuleSet.Version))
            .ThenByDescending(package => package.RuleSet.Version, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static List<TaxRulePackageSnapshot> LoadAvailablePackages()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "TaxRules", BundledFileName);
        if (!File.Exists(bundledPath))
            throw new InvalidOperationException(
                $"The bundled payroll tax package is missing: {bundledPath}");

        var bundled = LoadUnsignedPackage(File.ReadAllBytes(bundledPath), bundledPath, false);
        var packages = new List<TaxRulePackageSnapshot> { bundled };
        if (File.Exists(InstalledPackagePath))
            packages.Add(LoadSignedPackage(File.ReadAllBytes(InstalledPackagePath), InstalledPackagePath));
        return packages;
    }

    public static TaxRulePackageSnapshot LoadCurrent()
        => LoadLatestAvailable();

    public static async Task<TaxRuleUpdateResult> CheckForUpdatesAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        var current = LoadLatestAvailable();
        if (!force && !ShouldCheckNow())
            return new TaxRuleUpdateResult(
                false,
                false,
                "Tax rules were checked within the last 24 hours.",
                current);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("HisabKitabWorks", "1.0"));
            using var response = await client.GetAsync(GitHubLatestReleaseApi, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var asset = release.RootElement
                .GetProperty("assets")
                .EnumerateArray()
                .FirstOrDefault(candidate =>
                {
                    var name = candidate.GetProperty("name").GetString() ?? "";
                    return name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                           name.EndsWith(".hktax", StringComparison.OrdinalIgnoreCase);
                });

            SaveCheckState();
            if (asset.ValueKind == JsonValueKind.Undefined)
                return new TaxRuleUpdateResult(
                    true,
                    false,
                    "No signed payroll tax package is attached to the latest release.",
                    current);

            var downloadUrl = asset.GetProperty("browser_download_url").GetString()
                              ?? throw new InvalidOperationException("The tax-rule asset has no download URL.");
            var packageBytes = await client.GetByteArrayAsync(downloadUrl, cancellationToken);
            var candidate = LoadSignedPackage(packageBytes, downloadUrl);
            if (CompareVersions(candidate.RuleSet.Version, current.RuleSet.Version) <= 0)
                return new TaxRuleUpdateResult(
                    true,
                    false,
                    $"Payroll tax rules are current ({current.RuleSet.Version}).",
                    current);

            Directory.CreateDirectory(InstalledDirectory);
            var temporaryPath = InstalledPackagePath + ".new";
            await File.WriteAllBytesAsync(temporaryPath, packageBytes, cancellationToken);
            File.Move(temporaryPath, InstalledPackagePath, true);
            var installed = LoadSignedPackage(packageBytes, InstalledPackagePath);
            return new TaxRuleUpdateResult(
                true,
                true,
                $"Installed signed payroll tax rules {installed.RuleSet.Version}.",
                installed);
        }
        catch (Exception ex)
        {
            return new TaxRuleUpdateResult(
                true,
                false,
                $"Tax update check failed safely. Existing verified rules remain active. {ex.Message}",
                current);
        }
    }

    private static TaxRulePackageSnapshot LoadUnsignedPackage(
        byte[] payload,
        string path,
        bool isSignedUpdate)
    {
        var ruleSet = JsonSerializer.Deserialize<PayrollTaxRuleSet>(payload, JsonOptions)
                      ?? throw new InvalidOperationException("The payroll tax package is empty.");
        Validate(ruleSet);
        return new TaxRulePackageSnapshot(
            ruleSet,
            Convert.ToHexString(SHA256.HashData(payload)),
            path,
            isSignedUpdate);
    }

    private static TaxRulePackageSnapshot LoadSignedPackage(byte[] envelopeBytes, string path)
    {
        var envelope = JsonSerializer.Deserialize<SignedTaxRuleEnvelope>(envelopeBytes, JsonOptions)
                       ?? throw new InvalidOperationException("The signed payroll tax package is invalid.");
        if (envelope.Version != 1)
            throw new InvalidOperationException(
                $"Unsupported signed payroll tax package version {envelope.Version}.");

        var payload = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromBase64String(envelope.Signature);
        if (!DeviceLicenseService.VerifyAuthorizedSignature(payload, signature))
            throw new InvalidOperationException(
                "The payroll tax package signature is invalid. The update was rejected.");
        return LoadUnsignedPackage(payload, path, true);
    }

    private static void Validate(PayrollTaxRuleSet ruleSet)
    {
        if (ruleSet.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(ruleSet.RuleSetId) ||
            string.IsNullOrWhiteSpace(ruleSet.Version))
            throw new InvalidOperationException("The payroll tax package identity is invalid.");
        if (ruleSet.EffectiveTo < ruleSet.EffectiveFrom)
            throw new InvalidOperationException("The payroll tax package effective dates are invalid.");
        if (ruleSet.Federal.TaxYear != ruleSet.EffectiveFrom.Year)
            throw new InvalidOperationException("The federal tax year does not match the package.");
        if (ruleSet.States.Count != 51 ||
            ruleSet.States.Select(state => state.StateCode.ToUpperInvariant()).Distinct().Count() != 51)
            throw new InvalidOperationException(
                "The payroll tax package must contain all 50 states and the District of Columbia.");
    }

    private static bool ShouldCheckNow()
    {
        try
        {
            var path = Path.Combine(InstalledDirectory, CheckStateFileName);
            if (!File.Exists(path))
                return true;
            var state = JsonSerializer.Deserialize<TaxRuleUpdateState>(
                File.ReadAllText(path),
                JsonOptions);
            return state is null || DateTime.UtcNow - state.LastCheckedUtc >= TimeSpan.FromHours(24);
        }
        catch
        {
            return true;
        }
    }

    private static void SaveCheckState()
    {
        Directory.CreateDirectory(InstalledDirectory);
        var path = Path.Combine(InstalledDirectory, CheckStateFileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new TaxRuleUpdateState { LastCheckedUtc = DateTime.UtcNow },
                JsonOptions),
            Encoding.UTF8);
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) &&
            Version.TryParse(right, out var rightVersion))
            return leftVersion.CompareTo(rightVersion);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static Version ParseVersion(string value)
        => Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private sealed class SignedTaxRuleEnvelope
    {
        public int Version { get; set; }
        public string Payload { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    private sealed class TaxRuleUpdateState
    {
        public DateTime LastCheckedUtc { get; set; }
    }
}
