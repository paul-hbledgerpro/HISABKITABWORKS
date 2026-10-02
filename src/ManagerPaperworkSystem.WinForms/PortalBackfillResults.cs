using System.Diagnostics;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record PortalBackfillRequest(
    Guid Id, string BusinessName, string ReportName, DateOnly From, DateOnly Through,
    DateTime StartedUtc, int ProcessId = 0, DateTime? ProcessStartedUtc = null);
internal sealed record PortalBackfillResult(bool Success, string Message, DateTime CompletedUtc);

// Separate request and result files let a short-lived child finish before the
// launcher records its PID without either process overwriting the other's data.
internal static class PortalBackfillResults
{
    private static string DirectoryPath => Path.Combine(AppBootstrap.AppDataPath, "Backfill Results");
    private static string PathFor(Guid id, string suffix) => Path.Combine(DirectoryPath, $"{id:N}.{suffix}");

    public static PortalBackfillRequest Create(string business, string report, DateOnly from, DateOnly through)
    {
        var request = new PortalBackfillRequest(Guid.NewGuid(), business, report, from, through, DateTime.UtcNow);
        Write(PathFor(request.Id, "request.json"), request);
        return request;
    }

    public static void RegisterProcess(PortalBackfillRequest request, int processId, DateTime? startedUtc) =>
        Write(PathFor(request.Id, "request.json"), request with { ProcessId = processId, ProcessStartedUtc = startedUtc });

    public static void CancelStartup(Guid id) => File.Delete(PathFor(id, "request.json"));

    public static void Complete(Guid id, bool success, string message, bool overwrite = true) =>
        Write(PathFor(id, "result.json"), new PortalBackfillResult(success, message, DateTime.UtcNow), overwrite);

    private static void Write<T>(string path, T value, bool overwrite = true)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, path, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(path)) { /* The child completed during the exit check. */ }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static Notification? TakeNext()
    {
        if (!Directory.Exists(DirectoryPath)) return null;
        foreach (var requestPath in Directory.EnumerateFiles(DirectoryPath, "*.request.json").OrderBy(File.GetCreationTimeUtc))
        {
            FileStream? lease = null;
            try
            {
                var request = JsonSerializer.Deserialize<PortalBackfillRequest>(File.ReadAllText(requestPath));
                if (request is null || Path.GetFileName(requestPath) != $"{request.Id:N}.request.json") continue;
                var readPath = PathFor(request.Id, "read");
                if (File.Exists(readPath)) continue;
                lease = new FileStream(PathFor(request.Id, "notify.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(readPath)) continue;
                var resultPath = PathFor(request.Id, "result.json");
                if (!File.Exists(resultPath) && WasInterrupted(request))
                    Complete(request.Id, false,
                        "The background backfill stopped before reporting a result. Some reports may already have imported. " +
                        "Review the selected dates and retry the backfill.", overwrite: false);
                if (!File.Exists(resultPath)) continue;
                var result = JsonSerializer.Deserialize<PortalBackfillResult>(File.ReadAllText(resultPath));
                if (result is null) continue;
                var notification = new Notification(request, result, readPath, lease);
                lease = null; // Notification owns the cross-process display lease.
                return notification;
            }
            catch (IOException) { /* Another app window is displaying or writing this result. */ }
            catch (JsonException) { /* An invalid record must not interrupt normal app use. */ }
            finally { lease?.Dispose(); }
        }
        return null;
    }

    private static bool WasInterrupted(PortalBackfillRequest request)
    {
        if (DateTime.UtcNow - request.StartedUtc < TimeSpan.FromSeconds(30)) return false;
        if (request.ProcessId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(request.ProcessId);
            return process.HasExited || (request.ProcessStartedUtc.HasValue &&
                (process.StartTime.ToUniversalTime() - request.ProcessStartedUtc.Value).Duration() > TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal sealed class Notification(
        PortalBackfillRequest request, PortalBackfillResult result, string readPath, FileStream lease) : IDisposable
    {
        public PortalBackfillRequest Request { get; } = request;
        public PortalBackfillResult Result { get; } = result;
        public void Acknowledge() => File.WriteAllText(readPath, DateTime.UtcNow.ToString("O"));
        public void Dispose() => lease.Dispose();
    }
}
