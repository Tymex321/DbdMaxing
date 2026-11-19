using System.Runtime.InteropServices;
using DbdMaxing.Vision.Native;
using OpenCvSharp;

namespace DbdMaxing.Vision.Capture;

public enum CaptureStatus
{
    /// <summary>Nothing captured yet.</summary>
    None,
    Ok,
    /// <summary>The frame came back (almost) entirely black — typical for exclusive fullscreen.</summary>
    Black,
    WindowNotFound,
    Minimized,
    Failed,
}

/// <summary>
/// Grabs the pixels of a window's client area from the desktop (screen DC) with GDI BitBlt.
/// <para>
/// ANTI-CHEAT: this only copies what is already on screen. It never opens the game process, never reads its memory,
/// never injects or hooks. Works for Borderless / Windowed; exclusive fullscreen usually yields a black frame, which
/// is reported via <see cref="LastStatus"/> / <see cref="StatusChanged"/>.
/// </para>
/// <para>Because pixels come from the screen DC, the window must be visible and on top (the scanner only captures
/// while the game is the foreground window).</para>
/// </summary>
public sealed class WindowCapture
{
    public const string BlackFrameMessage = "Capture black — set DBD to Borderless/Windowed fullscreen";

    private static int _processDpiAttempted;

    public CaptureStatus LastStatus { get; private set; } = CaptureStatus.None;

    public string LastStatusMessage { get; private set; } = "";

    /// <summary>Screen rectangle (physical pixels) of the last capture attempt.</summary>
    public Rect LastCaptureRect { get; private set; }

    /// <summary>Raised when <see cref="LastStatus"/> changes. Raised on the capturing thread.</summary>
    public event EventHandler<CaptureStatus>? StatusChanged;

    /// <summary>
    /// Makes window rects and BitBlt use physical pixels. Call on the thread that will capture.
    /// <list type="bullet">
    /// <item>The calling thread is switched to PER_MONITOR_AWARE_V2 (thread-scoped, does not affect the UI).</item>
    /// <item>Once per process, if the process is still DPI-unaware, SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)
    /// is attempted. If awareness is already set (manifest / WPF) we leave it alone.</item>
    /// </list>
    /// Safe to call repeatedly; never throws.
    /// </summary>
    public static void EnsureDpiAwareness()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (Interlocked.Exchange(ref _processDpiAttempted, 1) == 0)
        {
            try
            {
                var ctx = NativeMethods.GetDpiAwarenessContextForProcess(IntPtr.Zero);
                var awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(ctx);
                if (awareness == NativeMethods.DPI_AWARENESS_UNAWARE)
                    NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch (Exception)
            {
                // EntryPointNotFound on very old Windows, or access denied: nothing we can do, thread-level below still helps.
            }
        }

        try
        {
            NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (Exception)
        {
            // Older than Windows 10 1607.
        }
    }

    /// <summary>
    /// Finds the largest visible, unowned top-level window belonging to <paramref name="processId"/> by enumerating
    /// windows (no process handle is opened). Returns <see cref="IntPtr.Zero"/> if none.
    /// </summary>
    public static IntPtr FindMainWindow(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != (uint)processId || !NativeMethods.IsWindowVisible(hWnd))
                return true;
            if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                return true;
            if (!NativeMethods.GetWindowRect(hWnd, out var r))
                return true;
            long area = (long)Math.Max(0, r.Width) * Math.Max(0, r.Height);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Returns true if the current foreground window belongs to <paramref name="processId"/>.</summary>
    public static bool IsProcessInForeground(int processId, out IntPtr foregroundWindow)
    {
        foregroundWindow = IntPtr.Zero;
        if (!OperatingSystem.IsWindows())
            return false;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid != (uint)processId)
            return false;
        foregroundWindow = fg;
        return true;
    }

    /// <summary>
    /// Screen rectangle (physical pixels when the thread is per-monitor DPI aware) of the window's client area,
    /// clipped to the visible frame (DWM extended frame bounds) and the virtual desktop. Null if unavailable.
    /// </summary>
    public static Rect? GetCaptureRect(IntPtr hWnd)
    {
        if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return null;

        Rect? frame = null;
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var ext,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0 && ext.Width > 0 && ext.Height > 0)
            frame = new Rect(ext.Left, ext.Top, ext.Width, ext.Height);
        else if (NativeMethods.GetWindowRect(hWnd, out var wr) && wr.Width > 0 && wr.Height > 0)
            frame = new Rect(wr.Left, wr.Top, wr.Width, wr.Height);

        Rect? client = null;
        if (NativeMethods.GetClientRect(hWnd, out var cr) && cr.Width > 0 && cr.Height > 0)
        {
            var origin = new NativeMethods.POINT { X = 0, Y = 0 };
            if (NativeMethods.ClientToScreen(hWnd, ref origin))
                client = new Rect(origin.X, origin.Y, cr.Width, cr.Height);
        }

        Rect rect;
        if (client is { } c && frame is { } f)
        {
            var inter = c & f;
            rect = inter.Width > 0 && inter.Height > 0 ? inter : c;
        }
        else if (client is { } c2)
            rect = c2;
        else if (frame is { } f2)
            rect = f2;
        else
            return null;

        var vs = new Rect(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        if (vs.Width > 0 && vs.Height > 0)
            rect &= vs;

        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    /// <summary>
    /// Captures the client area of <paramref name="hWnd"/> as a BGR <see cref="Mat"/> (caller disposes).
    /// Returns null on failure; check <see cref="LastStatus"/>. A black frame is still returned (status Black).
    /// </summary>
    public Mat? Capture(IntPtr hWnd)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            {
                SetStatus(CaptureStatus.WindowNotFound, "Game window not found");
                return null;
            }

            if (NativeMethods.IsIconic(hWnd))
            {
                SetStatus(CaptureStatus.Minimized, "Game window minimized");
                return null;
            }

            var rect = GetCaptureRect(hWnd);
            if (rect is null || rect.Value.Width < 64 || rect.Value.Height < 64)
            {
                SetStatus(CaptureStatus.WindowNotFound, "Game window has no visible area");
                return null;
            }

            LastCaptureRect = rect.Value;
            var mat = CaptureScreenRect(rect.Value);
            if (mat is null)
            {
                SetStatus(CaptureStatus.Failed, "Screen capture failed");
                return null;
            }

            if (IsBlackFrame(mat))
                SetStatus(CaptureStatus.Black, BlackFrameMessage);
            else
                SetStatus(CaptureStatus.Ok, "OK");
            return mat;
        }
        catch (Exception ex)
        {
            SetStatus(CaptureStatus.Failed, "Screen capture failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>BitBlt a rectangle of the desktop into a BGR Mat. Returns null on GDI failure.</summary>
    public static Mat? CaptureScreenRect(Rect rect)
    {
        if (!OperatingSystem.IsWindows() || rect.Width <= 0 || rect.Height <= 0)
            return null;

        int w = rect.Width, h = rect.Height;
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        Mat? bgra = null;
        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return null;
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                return null;
            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
            if (hBitmap == IntPtr.Zero)
                return null;

            old = NativeMethods.SelectObject(memDc, hBitmap);
            // SRCCOPY only (no CAPTUREBLT): avoids cursor flicker; the game is a normal, non-layered window.
            var ok = NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(memDc, old); // GetDIBits requires the bitmap not to be selected into a DC.
            old = IntPtr.Zero;
            if (!ok)
                return null;

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // negative = top-down rows, matching Mat layout
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            // 32bpp rows are always DWORD aligned, so a continuous CV_8UC4 Mat has exactly the DIB stride.
            bgra = new Mat(h, w, MatType.CV_8UC4);
            var lines = NativeMethods.GetDIBits(memDc, hBitmap, 0, (uint)h, bgra.Data, ref bmi, NativeMethods.DIB_RGB_COLORS);
            if (lines != h)
                return null;

            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            bgra?.Dispose();
            if (old != IntPtr.Zero && memDc != IntPtr.Zero)
                NativeMethods.SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero)
                NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero)
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>True if (nearly) every pixel is black. Cheap: works on a small downscale.</summary>
    public static bool IsBlackFrame(Mat frame, double maxLevel = 10)
    {
        if (frame.Empty())
            return true;
        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(160, 90), 0, 0, InterpolationFlags.Area);
        using var gray = new Mat();
        if (small.Channels() == 1)
            small.CopyTo(gray);
        else
            Cv2.CvtColor(small, gray, small.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out _, out double max);
        return max <= maxLevel;
    }

    private void SetStatus(CaptureStatus status, string message)
    {
        LastStatusMessage = message;
        if (LastStatus == status)
            return;
        LastStatus = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception)
        {
            // Never let a subscriber break capture.
        }
    }
}
