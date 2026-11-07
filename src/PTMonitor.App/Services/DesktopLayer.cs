using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using PTMonitor.Core.Diagnostics;

namespace PTMonitor.App.Services;

/// <summary>
/// Windows desktop layer: turns a window into a child of the shell's desktop
/// host window (<c>Progman</c> / <c>WorkerW</c>), the same technique desktop
/// widgets and wallpapers use. A window in that layer is
/// <list type="bullet">
/// <item>painted behind every application window,</item>
/// <item>not minimized by "Show desktop" (Win+D), and</item>
/// <item>unable to take the keyboard focus away from the app the user works in.</item>
/// </list>
/// <para>
/// Every call is guarded: off Windows the class reports "not available" and
/// the widget falls back to a normal always-on-top window. Nothing here ever
/// throws — a missing desktop host must cost the widget its desktop layer,
/// not the app.
/// </para>
/// </summary>
public static class DesktopLayer
{
    /// <summary>Whether this build can put a window into the desktop layer at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const long WsChild = 0x40000000;
    private const long WsPopup = 0x80000000;
    private const long WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    /// <summary>
    /// The shell window that owns the desktop: the <c>WorkerW</c> behind the
    /// icon view when there is one, otherwise the <c>WorkerW</c> that owns the
    /// icon view (Windows 11), otherwise <c>Progman</c>. Zero when the shell
    /// is not there.
    /// </summary>
    public static IntPtr FindDesktopHost()
    {
        if (!IsSupported) return IntPtr.Zero;
        try
        {
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;

            // The icon view ("SHELLDLL_DefView") normally hangs under Progman.
            // The WorkerW *behind* it is the host a desktop widget belongs to:
            // the card then sits above the wallpaper and below the icons.
            var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                var behind = FindWindowEx(IntPtr.Zero, defView, "WorkerW", null);
                if (behind != IntPtr.Zero) return behind;
            }

            // Windows 11 moved the icon view under a WorkerW. The WorkerW that
            // owns it is the host there.
            var owner = FindWorkerWindowOwningIcons();
            if (owner != IntPtr.Zero) return owner;

            return progman;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"desktop host lookup failed: {exception.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Makes the window a child of the desktop host: the style swap plus
    /// <c>SetParent</c> the shell expects, followed by a check that the parent
    /// really is the host. A failed attach is undone, so the caller can fall
    /// back to a plain window.
    /// </summary>
    public static bool Attach(IntPtr hwnd) => Attach(hwnd, out _);

    /// <summary>
    /// The same attach, with the host it used: the card is placed with the
    /// host's client coordinates, so the caller needs it.
    /// </summary>
    public static bool Attach(IntPtr hwnd, out IntPtr host)
    {
        host = IntPtr.Zero;
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        var desktop = FindDesktopHost();
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr((style & ~WsPopup) | WsChild));
            // SetParent returns the *previous* parent, not an error code, so
            // the result is checked by reading the parent back.
            SetParent(hwnd, desktop);
            if (GetParent(hwnd) != desktop)
            {
                Detach(hwnd);
                return false;
            }
            host = desktop;
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"desktop attach failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Undoes an attach: the window becomes a top-level window again.</summary>
    public static void Detach(IntPtr hwnd)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return;
        try
        {
            SetParent(hwnd, IntPtr.Zero);
            var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr((style & ~WsChild) | WsPopup));
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"desktop detach failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Moves the window inside its parent (the desktop host), so the position
    /// is relative to the host's client area. The result is verified: the
    /// parent must still be the host and the size must be the requested one.
    /// </summary>
    public static bool PlaceInHost(IntPtr hwnd, IntPtr host, int x, int y, int width, int height)
    {
        if (!IsSupported || hwnd == IntPtr.Zero || host == IntPtr.Zero) return false;
        try
        {
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                    SwpNoZOrder | SwpNoActivate))
                return false;
            if (GetParent(hwnd) != host) return false;
            if (!GetWindowRect(hwnd, out var rect)) return false;

            // The position is checked in the host's own coordinates:
            // GetWindowRect answers in screen coordinates, and the host does
            // not necessarily start at the virtual screen origin.
            var topLeft = new NativePoint { X = rect.Left, Y = rect.Top };
            if (ScreenToClient(host, ref topLeft) && (topLeft.X != x || topLeft.Y != y))
                return false;

            // The size may differ from the request when the card inherits the
            // host's DPI context. That is a note, not a failed placement: the
            // card is still in the desktop layer.
            if (rect.Right - rect.Left != width || rect.Bottom - rect.Top != height)
            {
                Log.Warn("widget",
                    $"the card is {rect.Right - rect.Left}x{rect.Bottom - rect.Top} px " +
                    $"instead of {width}x{height} (the desktop host's scaling)");
            }
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"desktop placement failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>The window's size in physical pixels.</summary>
    public static bool WindowSize(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        try
        {
            if (!GetWindowRect(hwnd, out var rect)) return false;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width > 0 && height > 0;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"window size query failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Moves a top-level window in screen coordinates (the fallback path, where
    /// the card is not a child of the desktop host). The rect is verified.
    /// </summary>
    public static bool PlaceOnScreen(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        try
        {
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                    SwpNoZOrder | SwpNoActivate))
                return false;
            if (!GetWindowRect(hwnd, out var rect)) return false;
            return rect.Left == x && rect.Top == y
                && rect.Right - rect.Left == width && rect.Bottom - rect.Top == height;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"screen placement failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clips the window to a rounded rectangle, so the card wears the rounded
    /// corners of a desktop widget <b>without</b> becoming a layered window: a
    /// layered (transparent) window cannot be a child of the desktop host.
    /// The corners are simply not painted, so the desktop shows through them.
    /// </summary>
    public static bool SetRoundedRegion(IntPtr hwnd, int width, int height, int radius)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        try
        {
            // The region is owned by the system once it is set on the window:
            // it must not be deleted.
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
            if (region == IntPtr.Zero) return false;
            return SetWindowRgn(hwnd, region, true);
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"rounded region failed: {exception.Message}");
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

    /// <summary>
    /// Moves the window into (or out of) the topmost band. Only the fallback
    /// window is ever topmost: a card in the desktop layer is behind every
    /// application window by construction.
    /// </summary>
    public static bool SetTopmost(IntPtr hwnd, bool topmost)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        try
        {
            return SetWindowPos(hwnd, topmost ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"topmost change failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Converts a screen position into the host's client coordinates — the
    /// coordinates a child window is placed with.
    /// </summary>
    public static bool ScreenToHost(IntPtr host, int screenX, int screenY, out int x, out int y)
    {
        x = screenX;
        y = screenY;
        if (!IsSupported || host == IntPtr.Zero) return false;
        try
        {
            var point = new NativePoint { X = screenX, Y = screenY };
            if (!ScreenToClient(host, ref point)) return false;
            x = point.X;
            y = point.Y;
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn("widget", $"coordinate conversion failed: {exception.Message}");
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
    /// The <c>WorkerW</c> window behind the one that owns the desktop icon
    /// view — the wallpaper layer, where a desktop widget belongs (below the
    /// icons). On Windows 11 the icon view no longer hangs under
    /// <c>Progman</c>, so the two windows are found by enumeration: the shell
    /// lists the top-level windows from the top of the z-order down, so the
    /// window that comes after the icon owner is the one behind it.
    /// </summary>
    private static IntPtr FindWorkerWindowOwningIcons()
    {
        var owner = IntPtr.Zero;
        var behind = IntPtr.Zero;
        var callback = new EnumWindowsProc((hwnd, _) =>
        {
            if (!string.Equals(ClassName(hwnd), "WorkerW", StringComparison.Ordinal)) return true;
            if (owner == IntPtr.Zero)
            {
                if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                    return true;
                // The icon owner: keep looking for the layer behind it.
                owner = hwnd;
                return true;
            }
            behind = hwnd;
            return false;   // stop the enumeration
        });
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return behind != IntPtr.Zero ? behind : owner;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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
    private static extern IntPtr SetParent(IntPtr hwnd, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

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
