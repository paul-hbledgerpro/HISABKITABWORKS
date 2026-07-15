using System.Runtime.InteropServices;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.UI.Services;

/// <summary>
/// QuestPDF uses native Skia dependencies.
/// As of this build, QuestPDF does NOT support Windows ARM64 (win-arm64),
/// so we must guard PDF generation on Snapdragon/Windows-on-ARM.
/// </summary>
internal static class PdfRuntime
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static Exception? _initError;

    public static void EnsureAvailableOrThrow()
    {
        if (_initialized)
        {
            if (_initError is not null)
                throw CreateNotSupported(_initError);
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                if (_initError is not null)
                    throw CreateNotSupported(_initError);
                return;
            }

            _initialized = true;

            // Fast-path: avoid triggering QuestPDF's static initializer on Windows ARM64,
            // because it will throw a TypeInitializationException during native dependency load.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                _initError = new PlatformNotSupportedException(
                    "PDF reporting is not available in the native ARM64 build (Snapdragon) because QuestPDF does not support win-arm64. " +
                    "Install the x64 build (it runs on Snapdragon via Windows x64 emulation) to enable PDF reports.");
                throw CreateNotSupported(_initError);
            }

            try
            {
                // Initialize QuestPDF lazily and safely.
                QuestPDF.Settings.License = LicenseType.Community;
            }
            catch (Exception ex)
            {
                _initError = ex;
                throw CreateNotSupported(ex);
            }
        }
    }

    private static NotSupportedException CreateNotSupported(Exception inner)
    {
        // Keep the message user-friendly for the UI (the detailed stack trace is already logged elsewhere).
        var msg = inner.Message;
        if (inner is TypeInitializationException or DllNotFoundException)
        {
            msg = "PDF reporting is not available on this Windows platform/build because the PDF engine (QuestPDF) could not load its native dependencies. " +
                  "If you are on Snapdragon/Windows ARM64, please use the x64 build (runs under emulation).";
        }

        return new NotSupportedException(msg, inner);
    }
}
