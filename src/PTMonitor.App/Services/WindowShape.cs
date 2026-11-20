using System.Runtime.InteropServices;
using PTMonitor.Core.Diagnostics;

namespace PTMonitor.App.Services;

/// <summary>
/// Window shaping: clips a top-level window to a circle.
/// <para>
/// The floating chip is a round badge, but Avalonia 11 on Win32 does no
/// per-pixel hit testing for a transparent window — a 64 × 64 transparent
/// window would swallow every click in the four corners the round body does
/// not cover. A <b>window region</b> is the Windows answer: it clips the
/// window's drawing <i>and</i> its hit area, so a corner click belongs to the
/// window underneath again.
/// </para>
/// <para>
/// Every call is guarded: off Windows the class reports "not available" and
/// nothing throws. A missing region costs the chip its click-through corners,
/// never the app.
/// </para>
/// </summary>
public static class WindowShape
{
    /// <summary>Whether this build can shape a window at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Clips <paramref name="hwnd"/> to a circle of the given diameter (device
    /// pixels), centred in the client area. Returns false when the platform has
    /// no regions, or the call failed — the caller keeps its rectangular window.
    /// </summary>
    public static bool ApplyCircle(IntPtr hwnd, double diameterInDevicePixels)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        var diameter = (int)Math.Round(diameterInDevicePixels);
        if (diameter <= 0) return false;
        try
        {
            // Centred, not anchored at the origin: the client rect can be a
            // pixel or two larger than the requested diameter after the
            // platform rounds the window size at the current scaling.
            GetClientRect(hwnd, out var client);
            var left = (client.Width - diameter) / 2;
            var top = (client.Height - diameter) / 2;
            var region = CreateEllipticRgn(left, top, left + diameter, top + diameter);
            if (region == IntPtr.Zero) return false;
            // On success the system owns the region: deleting it would be a
            // double free, so only the failure path releases it.
            if (SetWindowRgn(hwnd, region, true) != 0) return true;
            DeleteObject(region);
            return false;
        }
        catch (Exception exception)
        {
            Log.Warn("chip", $"window shaping failed: {exception.Message}");
            return false;
        }
    }

    // MARK: - Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
