using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbdMaxing.Core.Game;

/// <summary>
/// Window helpers for the vision scanner and the app. Uses only user32 window queries
/// and the OS window list; it never reads game memory. Returns false/zero off Windows.
/// </summary>
public static class ForegroundWindow
{
    /// <summary>True if the foreground window belongs to the given process.</summary>
    public static bool IsProcessInForeground(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return false;
        try
        {
            return IsProcessInForegroundWindows(pid);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The process's main window handle, or <see cref="IntPtr.Zero"/> if none or not on Windows.</summary>
    public static IntPtr GetMainWindowHandle(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return IntPtr.Zero;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProcessInForegroundWindows(int pid)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out var owner);
        return owner == (uint)pid;
    }

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
