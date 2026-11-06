using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using PTMonitor.Core.Diagnostics;

namespace PTMonitor.App.Services;

/// <summary>
/// The three states the tray icon can be in. They share one geometry — a flat
/// down arrow over a short bar — and differ only in colour.
/// </summary>
public enum TrayState
{
    Idle,
    Active,
    Error,
}

/// <summary>
/// Draws the tray icon at runtime, so the app ships without binary tray
/// assets.
///
/// The artwork is one flat silhouette — a bold down arrow over a bar, in the
/// spirit of the exe icon's sub-32 px variant but drawn separately — inside a
/// hairline rounded-square frame, so the icon reads as a square badge in the
/// tray. The whole thing is a single colour that follows the taskbar:
/// near-black on a light taskbar, white on a dark one. The floating chip
/// already shows the live rates, so the tray icon is an identity mark, not a
/// meter.
///
/// The icon is packaged as a real multi-size .ico in memory (16/20/24/32/48 px)
/// and handed to <see cref="WindowIcon"/> as a stream. That matters: a
/// <see cref="WindowIcon"/> built from a bitmap goes through
/// <c>CreateIconIndirect</c> with a 1-bit AND mask, which throws the alpha
/// channel away and leaves hard edges. A .ico with 32-bit entries keeps the
/// alpha, and Windows picks the entry that matches the taskbar size.
///
/// One native icon per (state, theme) is created and reused; see
/// <c>AppServices.UpdateTray</c> for why the icon must not be rebuilt per
/// poll tick./// </summary>
public static class TrayIconFactory
{
    /// <summary>
    /// The sizes Windows asks a tray icon for: 16 px at 100 % scaling up to
    /// 48 px at 300 %.
    /// </summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 48];

    // Glyph geometry, as a fraction of the icon side, so one description
    // covers every size. The arrow occupies the upper part, the bar sits
    // below it with a clear gap, and both fit inside the square frame.
    private const double ShaftHalf = 0.085;
    private const double ShaftTop = 0.19;
    private const double ShaftBottom = 0.41;
    private const double HeadHalf = 0.175;
    private const double HeadBottom = 0.41;
    private const double TipY = 0.62;
    private const double BarLeft = 0.26;
    private const double BarRight = 0.74;
    private const double BarTop = 0.71;
    private const double BarBottom = 0.82;

    // The square frame around the glyph: a rounded square stroked with a
    // hairline, so the icon reads as a square badge in the tray. The stroke is
    // 1 px at 16 px and never thicker than 2 px, and it sits one pixel inside
    // the icon edge so it is not clipped by it.
    private const double FrameStroke = 0.06;
    private const double FrameMargin = 1.0;
    // The *visible* corner radius of the frame. The stroked path is inset by
    // half the stroke, so the path radius is this minus half the stroke.
    // Keep it small: a hairline stroke cannot render a big radius at 16 px
    // without the corners turning into a staircase.
    private const double FrameRadius = 0.10;

    // Idle is the same glyph at a lower opacity -- the hue never changes.
    private const double IdleOpacity = 0.55;

    // Flat monochrome palette. The error colour is Fluent's critical
    // foreground, one per theme so it stays readable on both taskbars.
    private static readonly Color GlyphLight = Color.Parse("#1F1F1F");
    private static readonly Color GlyphDark = Color.Parse("#FFFFFF");
    private static readonly Color ErrorLight = Color.Parse("#C42B1C");
    private static readonly Color ErrorDark = Color.Parse("#FF99A4");

    // One native icon per (state, theme). Assigning a WindowIcon to the tray
    // makes the platform wrap it in a native icon (HICON), and the handles
    // behind it are never disposed by the tray. Creating a fresh one per
    // status change churned those handles until CreateIcon failed and took the
    // process down -- see .trellis/tasks/11-01-tray-icon-crash. The artwork
    // is static per state, so build it once.
    private static readonly ConcurrentDictionary<(TrayState State, bool Dark), WindowIcon> Icons = new();

    /// <summary>
    /// The tray icon for one state and theme, created on first use and reused
    /// afterwards.
    /// </summary>
    public static WindowIcon Icon(TrayState state, ThemeVariant variant)
    {
        var dark = variant == ThemeVariant.Dark;
        return Icons.GetOrAdd((state, dark), key => Create(key.State, key.Dark));
    }

    /// <summary>
    /// The whole icon as a .ico container, for inspection and tests.
    /// </summary>
    public static byte[] BuildIco(TrayState state, ThemeVariant variant) =>
        BuildIcoBytes(state, variant == ThemeVariant.Dark);

    private static byte[] BuildIcoBytes(TrayState state, bool dark)
    {
        var images = new List<(int Size, byte[] Bgra)>(Sizes.Length);
        foreach (var size in Sizes)
        {
            using var bitmap = Render(size, state, dark);
            images.Add((size, ToBgra(bitmap, size)));
        }
        return IcoEncoder.Encode(images);
    }

    /// <summary>
    /// Draws the glyph on a transparent background. The caller owns the
    /// returned bitmap and should dispose it once the pixels are copied.
    /// </summary>
    public static RenderTargetBitmap Render(int size, TrayState state, bool dark)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var color = state == TrayState.Error
                ? (dark ? ErrorDark : ErrorLight)
                : (dark ? GlyphDark : GlyphLight);
            var opacity = state == TrayState.Idle ? IdleOpacity : 1.0;
            DrawGlyph(context, size, color, opacity);
        }
        return bitmap;
    }

    private static WindowIcon Create(TrayState state, bool dark)
    {
        try
        {
            using var stream = new MemoryStream(BuildIcoBytes(state, dark));
            return new WindowIcon(stream);
        }
        catch (Exception exception)
        {
            // A malformed container must never leave the tray without an icon.
            // The fallback is the plain bitmap, which loses the alpha channel
            // (hard edges) but always loads. The bitmap stays alive: the
            // native icon keeps a reference to its pixels.
            Log.Warn("tray", $"tray icon packaging failed, falling back to a bitmap: {exception.Message}");
            return new WindowIcon(Render(32, state, dark));
        }
    }

    // MARK: - Glyph

    private static void DrawGlyph(DrawingContext context, int size, Color color, double opacity)
    {
        var s = (double)size;

        // The square frame: a hairline rounded square, centred on a path inset
        // by half the stroke (so the stroke straddles the path) plus a
        // one-pixel margin (so the frame is not clipped by the icon edge).
        // The path radius is the wanted visible radius minus half the stroke:
        // the stroke is centred on the path, so it adds that much outside.
        var stroke = Math.Clamp(Math.Round(s * FrameStroke), 1.0, 2.0);
        var inset = FrameMargin + stroke / 2;
        var path = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        var pathRadius = Math.Max(s * FrameRadius - stroke / 2, 0);
        context.DrawRectangle(null,
            new ImmutablePen(new ImmutableSolidColorBrush(color, opacity), stroke),
            new RoundedRect(path, pathRadius));

        var brush = new ImmutableSolidColorBrush(color, opacity);

        // One arrow: a shaft that widens into a head, tip pointing down.
        var arrow = new StreamGeometry();
        using (var geometry = arrow.Open())
        {
            geometry.BeginFigure(new Point((0.5 - ShaftHalf) * s, ShaftTop * s), true);
            geometry.LineTo(new Point((0.5 + ShaftHalf) * s, ShaftTop * s));
            geometry.LineTo(new Point((0.5 + ShaftHalf) * s, ShaftBottom * s));
            geometry.LineTo(new Point((0.5 + HeadHalf) * s, HeadBottom * s));
            geometry.LineTo(new Point(0.5 * s, TipY * s));
            geometry.LineTo(new Point((0.5 - HeadHalf) * s, HeadBottom * s));
            geometry.LineTo(new Point((0.5 - ShaftHalf) * s, ShaftBottom * s));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(brush, null, arrow);

        // The bar below the tip.
        var height = (BarBottom - BarTop) * s;
        var bar = new Rect(BarLeft * s, BarTop * s, (BarRight - BarLeft) * s, height);
        context.DrawRectangle(brush, null, new RoundedRect(bar, height / 2));
    }

    // MARK: - Pixels

    /// <summary>
    /// Copies a rendered bitmap into straight-alpha BGRA bytes -- the layout an
    /// icon entry needs. The render target is premultiplied, so the copy asks
    /// Avalonia to un-premultiply it; a premultiplied copy would darken every
    /// antialiased edge.
    /// </summary>
    private static byte[] ToBgra(RenderTargetBitmap bitmap, int size)
    {
        using var target = new WriteableBitmap(
            new PixelSize(size, size), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);

        using (var framebuffer = target.Lock())
        {
            bitmap.CopyPixels(framebuffer, AlphaFormat.Unpremul);

            var rowBytes = size * 4;
            var pixels = new byte[rowBytes * size];
            var row = new byte[rowBytes];
            for (var y = 0; y < size; y++)
            {
                Marshal.Copy(framebuffer.Address + y * framebuffer.RowBytes, row, 0, rowBytes);
                Buffer.BlockCopy(row, 0, pixels, y * rowBytes, rowBytes);
            }
            return pixels;
        }
    }
}
