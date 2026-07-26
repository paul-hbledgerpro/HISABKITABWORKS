using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record DatabaseCloudBackupResult(
    string BusinessName,
    bool Success,
    bool Uploaded,
    string Message);

internal sealed class DatabaseCloudBackupState
{
    public Dictionary<string, DateTime> LastSuccessfulUtc { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal static class DatabaseCloudBackupService
{
    private const string TaskName = "HISAB KITAB - Daily Database Cloud Backup";
    private static readonly byte[] StateEntropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-DATABASE-CLOUD-BACKUP-V1");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    private static string StatePath =>
        Path.Combine(AppBootstrap.AppDataPath, "database-cloud-backup-state.protected");

    private static string LogPath =>
        Path.Combine(AppBootstrap.AppDataPath, "Logs", "database-cloud-backup.log");

    public static bool IsConfigured => ResolveServiceUrl() is not null;

    public static void EnsureDailyTask()
    {
        if (!IsConfigured)
            return;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException(
                "The installed HISAB KITAB executable could not be located.");

        var temporaryXml = Path.Combine(
            Path.GetTempPath(),
            $"hisab-kitab-database-cloud-backup-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(
                temporaryXml,
                CreateScheduledTaskXml(executable),
                Encoding.Unicode);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "/Create",
                    "/TN", TaskName,
                    "/XML", temporaryXml,
                    "/F"
                }
            }) ?? throw new InvalidOperationException(
                "Windows Task Scheduler could not be started.");
            process.WaitForExit(20_000);
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(error))
                    error = process.StandardOutput.ReadToEnd().Trim();
                throw new InvalidOperationException(
                    "The daily database cloud-backup task could not be created. " +
                    (string.IsNullOrWhiteSpace(error)
                        ? "Run HISAB KITAB once as administrator."
                        : error));
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryXml);
            }
            catch
            {
                // Windows can remove an abandoned temporary task file later.
            }
        }
    }

    public static async Task<IReadOnlyList<DatabaseCloudBackupResult>> RunDueAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!await RunGate.WaitAsync(0, cancellationToken))
        {
            return
            [
                new DatabaseCloudBackupResult(
                    "",
                    true,
                    false,
                    "A database cloud backup is already running.")
            ];
        }

        FileStream? processLock = null;
        try
        {
            Directory.CreateDirectory(AppBootstrap.AppDataPath);
            try
            {
                processLock = new FileStream(
                    Path.Combine(AppBootstrap.AppDataPath, "database-cloud-backup.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                return
                [
                    new DatabaseCloudBackupResult(
                        "",
                        true,
                        false,
                        "A database cloud backup is already running on this PC.")
                ];
            }

            var endpoint = ResolveServiceUrl();
            if (endpoint is null)
            {
                return
                [
                    new DatabaseCloudBackupResult(
                        "",
                        false,
                        false,
                        "The developer database-backup service URL is not configured.")
                ];
            }

            var state = LoadState();
            var allBusinesses = LicensedBusinessService.Load();
            var businesses = StoreDirectoryPreferencesStore
                .GetOrderedBusinesses(allBusinesses)
                .Where(business => !string.IsNullOrWhiteSpace(business.DatabaseName))
                .ToList();
            var results = new List<DatabaseCloudBackupResult>();
            using var client = new DatabaseCloudBackupClient(endpoint);

            foreach (var business in businesses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stateKey = StoreDirectoryPreferencesStore.Key(business);
                if (!force &&
                    state.LastSuccessfulUtc.TryGetValue(stateKey, out var lastSuccess) &&
                    lastSuccess.ToLocalTime().Date >= DateTime.Today)
                {
                    continue;
                }

                string? localBackupPath = null;
                try
                {
                    WriteLog(
                        business.BusinessName,
                        true,
                        $"Creating verified native backup for {business.DatabaseName}.");
                    localBackupPath = await CreateVerifiedNativeBackupAsync(
                        business.DatabaseName,
                        cancellationToken);
                    var uploaded = await client.UploadAsync(
                        business,
                        localBackupPath,
                        cancellationToken);
                    state.LastSuccessfulUtc[stateKey] = DateTime.UtcNow;
                    SaveState(state);
                    var result = new DatabaseCloudBackupResult(
                        business.BusinessName,
                        true,
                        true,
                        $"Cloud backup uploaded for {business.BusinessName}: " +
                        $"{uploaded.Size / 1024d / 1024d:N1} MB, " +
                        $"object {uploaded.Key}.");
                    results.Add(result);
                    WriteLog(business.BusinessName, true, result.Message);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var message = AppBootstrap.RedactSensitiveText(exception.Message);
                    var result = new DatabaseCloudBackupResult(
                        business.BusinessName,
                        false,
                        false,
                        message);
                    results.Add(result);
                    WriteLog(business.BusinessName, false, message);
                }
                finally
                {
                    TryDelete(localBackupPath);
                }
            }

            return results;
        }
        finally
        {
            processLock?.Dispose();
            RunGate.Release();
        }
    }

    public static void WriteLog(
        string businessName,
        bool success,
        string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var safe = AppBootstrap.RedactSensitiveText(message)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{businessName}\t" +
                $"{(success ? "OK" : "FAILED")}\t{safe}{Environment.NewLine}");
        }
        catch
        {
            // Backup logging must never hide the actual backup result.
        }
    }

    private static async Task<string> CreateVerifiedNativeBackupAsync(
        string databaseName,
        CancellationToken cancellationToken)
    {
        var connectionString = LocalSqlServerPolicy.BuildConnectionString(databaseName);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 30,
            ApplicationName = "HISAB KITAB Cloud Backup"
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        string backupDirectory;
        await using (var pathCommand = connection.CreateCommand())
        {
            pathCommand.CommandText =
                "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'));";
            backupDirectory = (await pathCommand.ExecuteScalarAsync(cancellationToken)
                               as string)?.Trim() ?? "";
        }
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            throw new InvalidOperationException(
                "SQL Server did not report its native backup directory.");
        }

        var safeDatabase = SafeFilePart(databaseName);
        var backupPath = Path.Combine(
            backupDirectory,
            $"HKCLOUD_{safeDatabase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bak");
        try
        {
            var quotedDatabase = $"[{databaseName.Replace("]", "]]", StringComparison.Ordinal)}]";
            await using (var backup = connection.CreateCommand())
            {
                backup.CommandTimeout = 30 * 60;
                backup.CommandText =
                    $"BACKUP DATABASE {quotedDatabase} TO DISK=@path " +
                    "WITH COPY_ONLY, CHECKSUM, INIT, NAME=@name;";
                backup.Parameters.AddWithValue("@path", backupPath);
                backup.Parameters.AddWithValue(
                    "@name",
                    $"HISAB KITAB verified cloud backup {DateTime.UtcNow:O}");
                await backup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var verify = connection.CreateCommand())
            {
                verify.CommandTimeout = 30 * 60;
                verify.CommandText =
                    "RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
                verify.Parameters.AddWithValue("@path", backupPath);
                await verify.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
            {
                throw new InvalidOperationException(
                    "SQL Server reported success but the native backup file is empty or unavailable.");
            }
            return backupPath;
        }
        catch
        {
            TryDelete(backupPath);
            throw;
        }
    }

    private static Uri? ResolveServiceUrl()
    {
        var environment = Environment.GetEnvironmentVariable(
            "HISAB_KITAB_DATABASE_BACKUP_URL");
        if (!string.IsNullOrWhiteSpace(environment) &&
            Uri.TryCreate(environment.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var configured) &&
            configured.Scheme == Uri.UriSchemeHttps)
        {
            return configured;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "database-backup-service.url");
        if (!File.Exists(path))
            return null;
        var value = File.ReadAllText(path).Trim();
        return Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var bundled) &&
               bundled.Scheme == Uri.UriSchemeHttps
            ? bundled
            : null;
    }

    private static DatabaseCloudBackupState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new DatabaseCloudBackupState();
            var clear = ProtectedData.Unprotect(
                File.ReadAllBytes(StatePath),
                StateEntropy,
                DataProtectionScope.LocalMachine);
            try
            {
                return JsonSerializer.Deserialize<DatabaseCloudBackupState>(
                           clear,
                           JsonOptions)
                       ?? new DatabaseCloudBackupState();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return new DatabaseCloudBackupState();
        }
    }

    private static void SaveState(DatabaseCloudBackupState state)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                clear,
                StateEntropy,
                DataProtectionScope.LocalMachine);
            var temporary = StatePath + ".new";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, StatePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value
            .Where(character => !invalid.Contains(character))
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(safe) ? "database" : safe;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            WriteLog("", false, $"Could not remove temporary SQL backup {Path.GetFileName(path)}.");
        }
    }

    private static string CreateScheduledTaskXml(string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var userSid = WindowsIdentity.GetCurrent().User?.Value
                      ?? throw new InvalidOperationException(
                          "The current Windows user could not be identified for backup scheduling.");
        var start = DateTime.Today.AddHours(3)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description",
                        "Creates, verifies, and uploads private native SQL Server backups.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", start),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "ScheduleByDay",
                            new XElement(ns + "DaysInterval", "1")))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "WakeToRun", "true"),
                    new XElement(ns + "ExecutionTimeLimit", "PT2H"),
                    new XElement(ns + "RestartOnFailure",
                        new XElement(ns + "Interval", "PT30M"),
                        new XElement(ns + "Count", "3"))),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", "--database-cloud-backup"),
                        new XElement(ns + "WorkingDirectory",
                            Path.GetDirectoryName(executable) ?? "")))));
        return document.ToString(SaveOptions.DisableFormatting);
    }
}

internal sealed class DatabaseCloudBackupClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public DatabaseCloudBackupClient(Uri serviceUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = serviceUrl,
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public async Task<CloudBackupUploadResult> UploadAsync(
        LicensedBusinessConnection business,
        string path,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        string sha256;
        await using (var hashStream = File.OpenRead(path))
        {
            sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(hashStream, cancellationToken))
                .ToLowerInvariant();
        }

        var startBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                databaseName = business.DatabaseName,
                backupBytes = fileInfo.Length,
                sha256,
                createdUtc = DateTime.UtcNow.ToString("O")
            },
            JsonOptions);
        using var startRequest = CreateSignedRequest(
            business,
            HttpMethod.Post,
            "/api/backups/start",
            startBytes,
            "application/json");
        using var startResponse = await _http.SendAsync(startRequest, cancellationToken);
        var start = await ReadAsync<CloudBackupStartResponse>(
            startResponse,
            cancellationToken);

        var parts = new List<CloudBackupPart>();
        try
        {
            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                start.PartSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[start.PartSize];
            var partNumber = 1;
            while (true)
            {
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = await file.ReadAsync(
                        buffer.AsMemory(read, buffer.Length - read),
                        cancellationToken);
                    if (count == 0)
                        break;
                    read += count;
                }
                if (read == 0)
                    break;

                var partBytes = read == buffer.Length
                    ? buffer
                    : buffer[..read];
                var partPath =
                    $"/api/backups/part?key={Uri.EscapeDataString(start.Key)}" +
                    $"&uploadId={Uri.EscapeDataString(start.UploadId)}" +
                    $"&partNumber={partNumber}";
                using var partRequest = CreateSignedRequest(
                    business,
                    HttpMethod.Put,
                    partPath,
                    partBytes,
                    "application/octet-stream");
                using var partResponse = await _http.SendAsync(
                    partRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                parts.Add(await ReadAsync<CloudBackupPart>(
                    partResponse,
                    cancellationToken));
                partNumber++;
            }

            var completeBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    key = start.Key,
                    uploadId = start.UploadId,
                    parts
                },
                JsonOptions);
            using var completeRequest = CreateSignedRequest(
                business,
                HttpMethod.Post,
                "/api/backups/complete",
                completeBytes,
                "application/json");
            using var completeResponse = await _http.SendAsync(
                completeRequest,
                cancellationToken);
            return await ReadAsync<CloudBackupUploadResult>(
                completeResponse,
                cancellationToken);
        }
        catch
        {
            await TryAbortAsync(business, start, cancellationToken);
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task TryAbortAsync(
        LicensedBusinessConnection business,
        CloudBackupStartResponse start,
        CancellationToken cancellationToken)
    {
        try
        {
            var path =
                $"/api/backups/upload?key={Uri.EscapeDataString(start.Key)}" +
                $"&uploadId={Uri.EscapeDataString(start.UploadId)}";
            using var request = CreateSignedRequest(
                business,
                HttpMethod.Delete,
                path,
                [],
                contentType: null);
            using var response = await _http.SendAsync(request, cancellationToken);
        }
        catch
        {
            // R2 expires incomplete multipart uploads automatically.
        }
    }

    private static HttpRequestMessage CreateSignedRequest(
        LicensedBusinessConnection business,
        HttpMethod method,
        string path,
        byte[] body,
        string? contentType)
    {
        var license = LicenseRuntime.CurrentLicense
                      ?? throw new InvalidOperationException(
                          "A valid PC license is required for database cloud backup.");
        var storeGuid = string.IsNullOrWhiteSpace(business.StoreGuid)
            ? license.StoreGuid
            : business.StoreGuid;
        if (string.IsNullOrWhiteSpace(storeGuid))
        {
            throw new InvalidOperationException(
                "The licensed store identity is missing. Renew this PC license before cloud backup.");
        }
        var proof = DeviceLicenseService.CreateGatewayRequestProof(
            method.Method,
            path,
            storeGuid,
            license.CustomerId,
            license.LicenseId,
            body);
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (contentType is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        request.Headers.TryAddWithoutValidation("X-HK-Store-Guid", storeGuid);
        request.Headers.TryAddWithoutValidation(
            "X-HK-Customer-Id",
            license.CustomerId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            "X-HK-License-Id",
            license.LicenseId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-HK-Device-Id", proof.DeviceId);
        request.Headers.TryAddWithoutValidation("X-HK-Device-Name", proof.DeviceName);
        request.Headers.TryAddWithoutValidation("X-HK-Timestamp", proof.Timestamp);
        request.Headers.TryAddWithoutValidation("X-HK-Nonce", proof.Nonce);
        request.Headers.TryAddWithoutValidation("X-HK-Body-SHA256", proof.BodySha256);
        request.Headers.TryAddWithoutValidation("X-HK-Device-Proof", proof.DeviceProof);
        request.Headers.TryAddWithoutValidation("X-HK-License-Envelope", proof.LicenseEnvelope);
        request.Headers.TryAddWithoutValidation("X-HK-App-Version", Application.ProductVersion);
        return request;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(
                       JsonOptions,
                       cancellationToken)
                   ?? throw new InvalidOperationException(
                       "The database-backup service returned an empty response.");
        }

        var safe = $"HTTP {(int)response.StatusCode}";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<CloudBackupError>(
                JsonOptions,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Error))
                safe = error.Error;
        }
        catch
        {
            // Do not expose an untrusted HTML response from the public endpoint.
        }
        throw new InvalidOperationException(
            $"The protected database-backup service rejected the upload: {safe}");
    }

    private sealed record CloudBackupError(string Error);
}

internal sealed record CloudBackupStartResponse(
    string Key,
    string UploadId,
    int PartSize);

internal sealed record CloudBackupPart(
    int PartNumber,
    string Etag);

internal sealed record CloudBackupUploadResult(
    string Key,
    long Size,
    string Etag,
    DateTime Uploaded);
