using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
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
/// The artwork is one flat mark — a bold down arrow over a bar, in the spirit
/// of the exe icon's sub-32 px variant but drawn separately — knocked out of
/// a bright rounded tile.
///
/// The colours are fixed: the tile is white, the arrow is the dark ink, and
/// the error arrow is the dark red. The icon is the same in the light and the
/// dark theme — it does not follow the taskbar, because the bright tile
/// around a dark mark reads on both. The floating chip already shows the
/// live rates, so the tray icon is an identity mark, not a meter.
///
/// The icon is packaged as a real multi-size .ico in memory (16/20/24/32/48 px)
/// and handed to <see cref="WindowIcon"/> as a stream. That matters: a
/// <see cref="WindowIcon"/> built from a bitmap goes through
/// <c>CreateIconIndirect</c> with a 1-bit AND mask, which throws the alpha
/// channel away and leaves hard edges. A .ico with 32-bit entries keeps the
/// alpha, and Windows picks the entry that matches the taskbar size.
///
/// One native icon per state is created and reused; see
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
    // below it with a clear gap. The fractions describe the unscaled drawing:
    // the glyph itself is scaled about the icon centre (see GlyphScale).
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

    // The glyph fills the icon: there is no frame around it any more, so it
    // is scaled up about the centre to keep the mark legible at the 16 px
    // the taskbar asks for.
    private const double GlyphScale = 1.25;

    // The bright tile the glyph is knocked out of: a filled rounded square,
    // one pixel inside the icon edge (half a pixel at 16 px), with the same
    // corner radius the exe icon's card uses.
    private const double TileRadius = 0.22;
    private const double TileMargin = 1.0;

    // Idle is the same glyph at a lower opacity -- the hue never changes.
    private const double IdleOpacity = 0.55;

    // Flat palette. The artwork is theme-independent: a white tile with the
    // arrow knocked out of it in the dark ink, and the dark red for the error.
    private static readonly Color Tile = Color.Parse("#FFFFFF");
    private static readonly Color GlyphInk = Color.Parse("#1F1F1F");
    private static readonly Color ErrorInk = Color.Parse("#C42B1C");

    // One native icon per state. Assigning a WindowIcon to the tray makes the
    // platform wrap it in a native icon (HICON), and the handles behind it
    // are never disposed by the tray. Creating a fresh one per status change
    // churned those handles until CreateIcon failed and took the process down
    // -- see .trellis/tasks/11-01-tray-icon-crash. The artwork is static per
    // state, so build it once.
    private static readonly ConcurrentDictionary<TrayState, WindowIcon> Icons = new();

    /// <summary>
    /// The tray icon for one state, created on first use and reused
    /// afterwards.
    /// </summary>
    public static WindowIcon Icon(TrayState state) =>
        Icons.GetOrAdd(state, key => Create(key));

    /// <summary>
    /// The whole icon as a .ico container, for inspection and tests.
    /// </summary>
    public static byte[] BuildIco(TrayState state)
    {
        var images = new List<(int Size, byte[] Bgra)>(Sizes.Length);
        foreach (var size in Sizes)
        {
            using var bitmap = Render(size, state);
            images.Add((size, ToBgra(bitmap, size)));
        }
        return IcoEncoder.Encode(images);
    }

    /// <summary>
    /// Draws the icon on a transparent background. The caller owns the
    /// returned bitmap and should dispose it once the pixels are copied.
    /// </summary>
    public static RenderTargetBitmap Render(int size, TrayState state)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var opacity = state == TrayState.Idle ? IdleOpacity : 1.0;
            var ink = state == TrayState.Error ? ErrorInk : GlyphInk;
            // The glyph is knocked out of the bright tile, so the mark reads
            // as one bright piece around a dark arrow.
            DrawTile(context, size, opacity);
            DrawGlyph(context, size, ink, opacity);
        }
        return bitmap;
    }

    private static WindowIcon Create(TrayState state)
    {
        try
        {
            using var stream = new MemoryStream(BuildIco(state));
            return new WindowIcon(stream);
        }
        catch (Exception exception)
        {
            // A malformed container must never leave the tray without an icon.
            // The fallback is the plain bitmap, which loses the alpha channel
            // (hard edges) but always loads. The bitmap stays alive: the
            // native icon keeps a reference to its pixels.
            Log.Warn("tray", $"tray icon packaging failed, falling back to a bitmap: {exception.Message}");
            return new WindowIcon(Render(32, state));
        }
    }

    // MARK: - Tile

    /// <summary>
    /// The bright rounded tile the glyph is knocked out of. A filled rounded
    /// square, one pixel inside the icon edge (half a pixel at 16 px, where
    /// every pixel counts).
    /// </summary>
    private static void DrawTile(DrawingContext context, int size, double opacity)
    {
        var s = (double)size;
        var inset = size >= 32 ? TileMargin : TileMargin / 2;
        var rect = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        context.DrawRectangle(
            new ImmutableSolidColorBrush(Tile, opacity),
            null,
            new RoundedRect(rect, s * TileRadius));
    }

    // MARK: - Glyph

    private static void DrawGlyph(DrawingContext context, int size, Color color, double opacity)
    {
        var s = (double)size;

        // The glyph is drawn at its nominal fractions and scaled about the
        // icon centre, so the whole mark fills the tile.
        double X(double v) => (0.5 + (v - 0.5) * GlyphScale) * s;
        double Y(double v) => (0.5 + (v - 0.5) * GlyphScale) * s;

        var brush = new ImmutableSolidColorBrush(color, opacity);

        // One arrow: a shaft that widens into a head, tip pointing down.
        var arrow = new StreamGeometry();
        using (var geometry = arrow.Open())
        {
            geometry.BeginFigure(new Point(X(0.5 - ShaftHalf), Y(ShaftTop)), true);
            geometry.LineTo(new Point(X(0.5 + ShaftHalf), Y(ShaftTop)));
            geometry.LineTo(new Point(X(0.5 + ShaftHalf), Y(ShaftBottom)));
            geometry.LineTo(new Point(X(0.5 + HeadHalf), Y(HeadBottom)));
            geometry.LineTo(new Point(X(0.5), Y(TipY)));
            geometry.LineTo(new Point(X(0.5 - HeadHalf), Y(HeadBottom)));
            geometry.LineTo(new Point(X(0.5 - ShaftHalf), Y(ShaftBottom)));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(brush, null, arrow);

        // The bar below the tip.
        var height = (BarBottom - BarTop) * s * GlyphScale;
        var bar = new Rect(X(BarLeft), Y(BarTop), (BarRight - BarLeft) * s * GlyphScale, height);
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
