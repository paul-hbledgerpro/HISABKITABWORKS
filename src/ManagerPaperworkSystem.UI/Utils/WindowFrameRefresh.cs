using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ManagerPaperworkSystem.UI.Utils;

/// <summary>
/// Forces a non-client area refresh. On some Windows setups, the caption buttons
/// may not paint until the first resize; SWP_FRAMECHANGED fixes that.
/// </summary>
public static class WindowFrameRefresh
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    public static void Refresh(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
        catch
        {
            // Ignore; purely a visual refresh.
        }
    }
}
