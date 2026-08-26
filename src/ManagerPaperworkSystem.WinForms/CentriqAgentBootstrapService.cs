using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ManagerPaperworkSystem.WinForms;

internal static class CentriqAgentBootstrapService
{
    private const string ApiBaseUrl = "https://api.centriqtechnologies.com/";
    private const string ProductCode = "HISAB-KITAB";
    private static readonly Guid ProductId = Guid.Parse("e27471dd-da54-4979-b3fd-cb524462cfd7");
    private const string SigningKeyId = "production-2026";
    private const string BundledAgentMsiSha256 = "D16711E9AA457EEF817ADB19576737EF3C06CB0A6C46C3FF6234C0C7B14DC8FA";
    private static readonly byte[] AgentSecretEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("Centriq.ControlHub.Agent/v1"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string AgentExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Centriq", "Agent", "Centriq.ControlHub.Agent.exe");
    private static string AgentSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Centriq", "Agent", "agent.json");
    private static string BundledAgentMsiPath => Path.Combine(
        AppContext.BaseDirectory, "Centriq", "Centriq-Agent-x64.msi");
    private static string LogPath => Path.Combine(AppBootstrap.AppDataPath, "centriq-agent-bootstrap.log");
    private static string PendingAgentSettingsPath => Path.Combine(
        AppBootstrap.AppDataPath, "CentriqBootstrap", "pending-agent-settings.json");

    public static bool TryHandleElevatedCommand(string[] args)
    {
        if (args.Length != 2 || !args[0].Equals("--centriq-agent-finalize", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            FinalizeElevated(Path.GetFullPath(args[1]));
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            TryLog("Elevated Agent finalization failed", exception);
            Environment.ExitCode = 1;
        }
        return true;
    }

    public static void TryEnsureForLicensedDevice()
    {
        if (DemoRuntime.IsEnabled || LicenseRuntime.CurrentLicense is null)
            return;
        try
        {
            if (IsCorrectlyConfigured())
                return;
            if (!File.Exists(BundledAgentMsiPath))
            {
                TryLog("The bundled Centriq Agent installer is unavailable.");
                return;
            }
            VerifyBundledMsi();

            string? stagedSettingsPath = null;
            if (!File.Exists(AgentSettingsPath))
                stagedSettingsPath = File.Exists(PendingAgentSettingsPath)
                    ? PendingAgentSettingsPath
                    : EnrollAndStageSettingsAsync().GetAwaiter().GetResult();
            RunElevatedFinalizer(stagedSettingsPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            TryLog("Windows administrator approval for the Centriq Agent was cancelled.");
        }
        catch (Exception exception)
        {
            // The accounting application remains available. Enrollment retries
            // automatically on the next normal startup.
            TryLog("Automatic Centriq Agent setup will retry", exception);
        }
    }

    private static bool IsCorrectlyConfigured()
    {
        if (!File.Exists(AgentExecutablePath) || !File.Exists(AgentSettingsPath))
            return false;
        try
        {
            var settings = JsonSerializer.Deserialize<AgentSettingsSnapshot>(
                File.ReadAllText(AgentSettingsPath), JsonOptions);
            if (settings is null || !settings.ApiBaseUrl.Equals(ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
                return false;
            return settings.ProductInstallRoots.TryGetValue(ProductId, out var root)
                   && SameDirectory(root, AppContext.BaseDirectory)
                   && settings.SigningPublicKeys.TryGetValue(SigningKeyId, out var publicKey)
                   && IsValidReleasePublicKey(publicKey);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> EnrollAndStageSettingsAsync()
    {
        var license = LicenseRuntime.CurrentLicense
            ?? throw new InvalidOperationException("A valid HISAB KITAB device license is required.");
        using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = Convert.ToBase64String(agentKey.ExportPkcs8PrivateKey());
        var request = new TrustedAgentRequest(
                Environment.MachineName,
                ComputeMachineFingerprint(),
                agentKey.ExportSubjectPublicKeyInfoPem(),
                Environment.OSVersion.VersionString,
                Environment.OSVersion.Version.Build,
                "x64",
                "1.4.9");
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var path = $"api/v1/agent/product-enroll/{ProductCode}";
        var proof = DeviceLicenseService.CreateGatewayRequestProof(
            "POST",
            path,
            license.StoreGuid,
            license.CustomerId,
            license.LicenseId,
            requestBytes);
        var payload = new TrustedEnrollmentRequest(
            request,
            new ProductProof(
                proof.DeviceId,
                proof.Timestamp,
                proof.Nonce,
                proof.BodySha256,
                proof.DeviceProof,
                proof.LicenseEnvelope));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var response = await client.PostAsJsonAsync(path, payload, JsonOptions);
        response.EnsureSuccessStatusCode();
        var enrolled = await response.Content.ReadFromJsonAsync<TrustedEnrollmentResponseBody>(JsonOptions)
            ?? throw new InvalidOperationException("Centriq returned an empty enrollment response.");
        if (enrolled.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(enrolled.DeviceCredential))
            throw new InvalidOperationException("Centriq returned an incomplete enrollment response.");
        if (enrolled.ProductId != ProductId)
            throw new InvalidOperationException("Centriq returned an unexpected HISAB KITAB product identity.");
        if (!enrolled.SigningKeyId.Equals(SigningKeyId, StringComparison.Ordinal)
            || !IsValidReleasePublicKey(enrolled.SigningPublicKeyPem))
            throw new InvalidOperationException("Centriq returned an invalid release-verification key.");

        var settings = new AgentSettingsSnapshot(
            ApiBaseUrl,
            enrolled.DeviceId,
            ProtectAgentSecret(enrolled.DeviceCredential),
            ProtectAgentSecret(privateKey),
            new Dictionary<Guid, string> { [ProductId] = Path.GetFullPath(AppContext.BaseDirectory) },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [enrolled.SigningKeyId] = enrolled.SigningPublicKeyPem.Trim()
            });
        var bootstrapDirectory = Path.Combine(AppBootstrap.AppDataPath, "CentriqBootstrap");
        Directory.CreateDirectory(bootstrapDirectory);
        var temporary = PendingAgentSettingsPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, PendingAgentSettingsPath, true);
        return PendingAgentSettingsPath;
    }

    private static void RunElevatedFinalizer(string? stagedSettingsPath)
    {
        var request = new ElevatedRequest(stagedSettingsPath);
        var bootstrapDirectory = Path.Combine(AppBootstrap.AppDataPath, "CentriqBootstrap");
        Directory.CreateDirectory(bootstrapDirectory);
        var requestPath = Path.Combine(bootstrapDirectory, $"finalize-{Guid.NewGuid():N}.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, JsonOptions));
        var completed = false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };
            startInfo.ArgumentList.Add("--centriq-agent-finalize");
            startInfo.ArgumentList.Add(requestPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the Centriq Agent installer.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The Centriq Agent installer did not complete successfully.");
            completed = true;
        }
        finally
        {
            TryDelete(requestPath);
            if (completed && !string.IsNullOrWhiteSpace(stagedSettingsPath))
                TryDelete(stagedSettingsPath);
        }
    }

    private static void FinalizeElevated(string requestPath)
    {
        if (!IsAdministrator())
            throw new InvalidOperationException("Centriq Agent installation requires Windows administrator approval.");
        VerifyBundledMsi();
        var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestPath), JsonOptions)
            ?? throw new InvalidOperationException("The Centriq Agent setup request is empty.");

        StopAgentService();
        Directory.CreateDirectory(Path.GetDirectoryName(AgentSettingsPath)!);
        if (!string.IsNullOrWhiteSpace(request.StagedSettingsPath))
        {
            var stagedPath = Path.GetFullPath(request.StagedSettingsPath);
            var settings = JsonSerializer.Deserialize<AgentSettingsSnapshot>(File.ReadAllText(stagedPath), JsonOptions)
                ?? throw new InvalidOperationException("The staged Centriq Agent settings are empty.");
            ValidateSettings(settings);
            WriteSettings(settings);
        }
        else
        {
            var settings = JsonSerializer.Deserialize<AgentSettingsSnapshot>(
                File.ReadAllText(AgentSettingsPath), JsonOptions)
                ?? throw new InvalidOperationException("The installed Centriq Agent settings are empty.");
            ValidateExistingEnrollment(settings);
            settings.ProductInstallRoots[ProductId] = Path.GetFullPath(AppContext.BaseDirectory);
            if (!settings.SigningPublicKeys.TryGetValue(SigningKeyId, out var installedKey)
                || !IsValidReleasePublicKey(installedKey))
                throw new InvalidOperationException("The installed Centriq Agent is missing its release-verification key.");
            WriteSettings(settings);
        }

        if (!File.Exists(AgentExecutablePath))
        {
            var install = RunProcess("msiexec.exe", ["/i", BundledAgentMsiPath, "/qn", "/norestart"]);
            if (install != 0 || !File.Exists(AgentExecutablePath))
                throw new InvalidOperationException($"Centriq Agent installation failed with Windows Installer code {install}.");
        }
        _ = RunProcess("sc.exe", ["start", "Centriq Device Agent"]);
    }

    private static void ValidateSettings(AgentSettingsSnapshot settings)
    {
        ValidateExistingEnrollment(settings);
        if (settings.ProductInstallRoots.Count != 1
            || !settings.ProductInstallRoots.TryGetValue(ProductId, out var root)
            || !SameDirectory(root, AppContext.BaseDirectory)
            || settings.SigningPublicKeys.Count != 1
            || !settings.SigningPublicKeys.TryGetValue(SigningKeyId, out var publicKey)
            || !IsValidReleasePublicKey(publicKey))
            throw new InvalidOperationException("The staged Centriq Agent product configuration is invalid.");
    }

    private static void ValidateExistingEnrollment(AgentSettingsSnapshot settings)
    {
        if (!settings.ApiBaseUrl.Equals(ApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            || settings.DeviceId == Guid.Empty
            || string.IsNullOrWhiteSpace(settings.ProtectedDeviceCredential)
            || string.IsNullOrWhiteSpace(settings.ProtectedDevicePrivateKey))
            throw new InvalidOperationException("The Centriq Agent enrollment settings are invalid.");
        _ = Convert.FromBase64String(settings.ProtectedDeviceCredential);
        _ = Convert.FromBase64String(settings.ProtectedDevicePrivateKey);
    }

    private static void WriteSettings(AgentSettingsSnapshot settings)
    {
        var temporary = AgentSettingsPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, AgentSettingsPath, true);
    }

    private static void VerifyBundledMsi()
    {
        if (!File.Exists(BundledAgentMsiPath))
            throw new FileNotFoundException("The bundled Centriq Agent installer was not found.", BundledAgentMsiPath);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BundledAgentMsiPath)));
        if (!actual.Equals(BundledAgentMsiSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bundled Centriq Agent installer failed integrity verification.");
    }

    private static string ComputeMachineFingerprint()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var machineGuid = key?.GetValue("MachineGuid") as string
            ?? throw new InvalidOperationException("Windows MachineGuid is unavailable.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{machineGuid}|x64")));
    }

    private static string ProtectAgentSecret(string value) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), AgentSecretEntropy, DataProtectionScope.LocalMachine));

    private static bool IsValidReleasePublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(value);
            return key.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool SameDirectory(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void StopAgentService() => _ = RunProcess("sc.exe", ["stop", "Centriq Device Agent"]);

    private static int RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Windows could not start {fileName}.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void TryLog(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(AppBootstrap.AppDataPath);
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:O} {message}{(exception is null ? "" : $": {AppBootstrap.RedactSensitiveText(exception.Message)}")}{Environment.NewLine}");
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record TrustedAgentRequest(
        string DeviceName,
        string MachineFingerprintSha256,
        string PublicKeyPem,
        string OperatingSystem,
        int WindowsBuild,
        string Architecture,
        string AgentVersion);
    private sealed record ProductProof(
        string DeviceId,
        string Timestamp,
        string Nonce,
        string BodySha256,
        string DeviceProof,
        string LicenseEnvelope);
    private sealed record TrustedEnrollmentRequest(TrustedAgentRequest Agent, ProductProof Proof);
    private sealed record TrustedEnrollmentResponseBody(
        Guid DeviceId,
        string DeviceCredential,
        Guid ProductId,
        string SigningKeyId,
        string SigningPublicKeyPem);
    private sealed record ElevatedRequest(string? StagedSettingsPath);
    private sealed record AgentSettingsSnapshot(
        string ApiBaseUrl,
        Guid DeviceId,
        string ProtectedDeviceCredential,
        string ProtectedDevicePrivateKey,
        Dictionary<Guid, string> ProductInstallRoots,
        Dictionary<string, string> SigningPublicKeys);
}
