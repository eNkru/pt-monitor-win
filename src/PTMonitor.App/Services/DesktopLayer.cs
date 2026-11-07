using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using PTMonitor.Core.Diagnostics;

namespace PTMonitor.App.Services;

/// <summary>
/// Windows desktop layer: keeps a window directly above the shell's desktop
/// window and below every application window.
/// <para>
/// The window is deliberately <b>not</b> re-parented into the desktop host
/// (<c>Progman</c> / <c>WorkerW</c>): Avalonia renders a transparent window as
/// a window without a redirection bitmap (DirectComposition) or with DWM
/// blur-behind, and neither survives being made a child of the shell's
/// desktop — the card would simply not be drawn. Pinning the z-order instead
/// needs no re-parenting, so per-pixel transparency keeps working, and the
/// card still sits behind every application window.
/// </para>
/// <para>
/// Every call is guarded: off Windows the class reports "not available" and
/// nothing throws. A missing desktop host costs the card its z-order position,
/// never the app.
/// </para>
/// </summary>
public static class DesktopLayer
{
    /// <summary>Whether this build can put a window into the desktop layer at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// Places the window directly above the desktop, below every application
    /// window. Returns false when the shell's desktop window cannot be found
    /// (a shell restart, or a non-Windows build) — the caller keeps the window
    /// as it is and tries again later.
    /// </summary>
    public static bool PinAboveDesktop(IntPtr hwnd)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        var host = FindDesktopIconHost();
        if (host == IntPtr.Zero) return false;
        try
        {
            // hWndInsertAfter is the window the positioned window goes above,
            // so the card lands directly on top of the desktop and underneath
            // everything that opened after it.
            return SetWindowPos(hwnd, host, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"desktop pin failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds <c>WS_EX_NOACTIVATE</c>, so clicking the card never activates it
    /// and never raises it above the app the user is working in.
    /// </summary>
    public static bool MakeNonActivating(IntPtr hwnd)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        try
        {
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate));
            return (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExNoActivate) != 0;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"non-activating style failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>The cursor position in physical screen pixels, or null off Windows.</summary>
    public static PixelPoint? CursorPosition()
    {
        if (!IsSupported) return null;
        try
        {
            return GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The top-level window that carries the desktop icons: <c>Progman</c> when
    /// the icon view (<c>SHELLDLL_DefView</c>) hangs under it, otherwise the
    /// <c>WorkerW</c> that owns it. Pinning above <i>this</i> window puts the
    /// card over the icons and wallpaper but below every application window.
    /// </summary>
    private static IntPtr FindDesktopIconHost()
    {
        try
        {
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;
            if (FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return progman;

            // Windows 11 keeps the icon view under a WorkerW instead.
            var owner = FindWorkerWindowOwningIcons();
            return owner != IntPtr.Zero ? owner : progman;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"desktop host lookup failed: {exception.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>The <c>WorkerW</c> window that owns the desktop icon view.</summary>
    private static IntPtr FindWorkerWindowOwningIcons()
    {
        var found = IntPtr.Zero;
        var callback = new EnumWindowsProc((hwnd, _) =>
        {
            if (!string.Equals(ClassName(hwnd), "WorkerW", StringComparison.Ordinal)) return true;
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            found = hwnd;
            return false;   // stop the enumeration
        });
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    // MARK: - Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    /// <summary>
    /// The window style accessors, 32- and 64-bit aware: <c>SetWindowLongPtr</c>
    /// is a macro on 32-bit Windows and only <c>SetWindowLong</c> is exported.
    /// </summary>
    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, value);
        else SetWindowLong32(hwnd, index, value.ToInt32());
    }
}
