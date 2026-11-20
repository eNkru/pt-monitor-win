using System.Runtime.InteropServices;
using PTMonitor.Core.Diagnostics;

namespace PTMonitor.App.Services;

/// <summary>
/// Window shaping: clips a top-level window to a rounded rectangle.
/// <para>
/// The floating chip is a small screen with rounded corners, but Avalonia 11 on
/// Win32 does no per-pixel hit testing for a transparent window — a
/// transparent window would swallow every click in the four corners the round
/// screen does not cover. A <b>window region</b> is the Windows answer: it
/// clips the window's drawing <i>and</i> its hit area, so a corner click
/// belongs to the window underneath again.
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
    /// Clips <paramref name="hwnd"/> to a rounded rectangle of the given size
    /// (device pixels), centred in the client area, with the given corner
    /// radius. The region is drawn one pixel **larger** than the body on every
    /// side: a region's edge is aliased, so it must stay outside the body's
    /// antialiased edge instead of cutting it.
    /// </summary>
    /// <returns>false when the platform has no regions, or the call failed —
    /// the caller keeps its rectangular window.</returns>
    public static bool ApplyRoundedRect(
        IntPtr hwnd,
        int widthInDevicePixels,
        int heightInDevicePixels,
        int cornerRadiusInDevicePixels)
    {
        if (!IsSupported || hwnd == IntPtr.Zero) return false;
        if (widthInDevicePixels <= 0 || heightInDevicePixels <= 0) return false;
        try
        {
            GetClientRect(hwnd, out var client);
            var left = (client.Width - widthInDevicePixels) / 2;
            var top = (client.Height - heightInDevicePixels) / 2;
            // One pixel of slack on every side: the region's aliased edge then
            // sits outside the body's antialiased edge instead of cutting it.
            var region = CreateRoundRectRgn(
                left - 1,
                top - 1,
                left + widthInDevicePixels + 1,
                top + heightInDevicePixels + 1,
                (cornerRadiusInDevicePixels + 1) * 2,
                (cornerRadiusInDevicePixels + 1) * 2);
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
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
