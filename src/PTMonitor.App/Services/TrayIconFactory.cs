using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace PTMonitor.App.Services;

/// <summary>
/// Renders the tray icons at runtime with vector drawing, so the app ships
/// without binary tray assets. All three states share one geometry: a flat,
/// light rounded tile with a hairline ring, a down/up arrow pair and a pill
/// progress meter — the Windows app-icon artwork drawn by
/// <c>scripts/make-app-icon.py</c>, which must be edited together with this
/// file. The states differ only in colour: idle dims both arrows and shows
/// only the pill track; active is the app icon proper (solid blue down arrow,
/// pill 62% filled); error swaps blue for red, fills the pill and outlines the
/// tile in red.
/// </summary>
public enum TrayState
{
    Idle,
    Active,
    Error,
}

public static class TrayIconFactory
{
    private const int Size = 32;

    // Flat, light palette — deliberately not the macOS charcoal card. Keep in
    // sync with the constants at the top of scripts/make-app-icon.py.
    private static readonly Color Tile = Color.Parse("#F2F6FA");
    private static readonly Color Border = Color.Parse("#D3DEE9");
    private static readonly Color Accent = Color.Parse("#0F6CBD");
    private static readonly Color Danger = Color.Parse("#C42B1C");

    private const double CardRadius = 0.22;
    private const double CardBorder = 0.030;
    private const double ErrorBorder = 0.050;
    private const double Dim = 0.40;
    private const double TrackAlpha = 0.14;
    private const double PillFill = 0.62;

    private const double BandTop = 0.32;
    private const double BandBottom = 0.66;
    private const double ShaftHalf = 0.043;
    private const double HeadHalf = 0.105;
    private const double HeadDepth = 0.105;
    private const double DownCx = 0.37;
    private const double UpCx = 0.63;
    private const double PillWidth = 0.52;
    private const double PillHeight = 0.042;
    private const double PillY = 0.785;

    public static WindowIcon Idle() => new(Render(TrayState.Idle));
    public static WindowIcon Active() => new(Render(TrayState.Active));
    public static WindowIcon Error() => new(Render(TrayState.Error));

    /// <summary>Renders the shared tile/arrows/pill artwork for one state.</summary>
    public static RenderTargetBitmap Render(TrayState state)
    {
        var s = (double)Size;
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var accent = state == TrayState.Error ? Danger : Accent;

            DrawTile(context, s, state == TrayState.Error);

            var solid = state != TrayState.Idle;
            DrawArrow(context, s, down: true,
                new ImmutableSolidColorBrush(accent, solid ? 1.0 : Dim));
            DrawArrow(context, s, down: false,
                new ImmutableSolidColorBrush(accent, Dim));

            // Pill: empty track when idle, 62% blue when active, full red on error.
            double? fill = state switch
            {
                TrayState.Active => PillFill,
                TrayState.Error => 1.0,
                _ => null,
            };
            DrawPill(context, s, fill, accent);
        }
        return bitmap;
    }

    // MARK: - Tile

    private static void DrawTile(DrawingContext context, double s, bool isError)
    {
        var rect = new Rect(0, 0, s, s);
        var radius = CardRadius * s;
        var ring = (isError ? ErrorBorder : CardBorder) * s;

        // Flat fill: no gradient, no sheen, no shadow.
        context.DrawRectangle(new ImmutableSolidColorBrush(Tile), null,
            new RoundedRect(rect, radius));

        // Ring as a stroke centred on a path inset by half the pen width, so
        // the stroke's outer edge lands exactly on the tile edge. The path
        // corner radius shrinks with the inset too -- leaving it at `radius`
        // would bulge the stroke past the tile at the four corners. scripts/
        // make-app-icon.py models the ring the same way.
        var inset = ring / 2;
        var path = new Rect(inset, inset, s - ring, s - ring);
        var pathRadius = Math.Max(radius - inset, 0);
        var ringColor = isError ? Danger : Border;
        context.DrawRectangle(null, new ImmutablePen(new ImmutableSolidColorBrush(ringColor), ring),
            new RoundedRect(path, pathRadius));
    }

    // MARK: - Arrows

    private static void DrawArrow(DrawingContext context, double s, bool down, IBrush brush)
    {
        var bandTop = BandTop * s;
        var bandBottom = BandBottom * s;
        var cx = (down ? DownCx : UpCx) * s;
        var shaft = ShaftHalf * s;
        var head = HeadHalf * s;
        var depth = HeadDepth * s;

        // Tip at the arrow's pointing end; the shaft starts at the opposite end.
        var tipY = down ? bandBottom : bandTop;
        var baseY = down ? bandBottom - depth : bandTop + depth;
        var farY = down ? bandTop : bandBottom;

        var arrow = new StreamGeometry();
        using (var geometry = arrow.Open())
        {
            geometry.BeginFigure(new Point(cx - shaft, farY), true);
            geometry.LineTo(new Point(cx + shaft, farY));
            geometry.LineTo(new Point(cx + shaft, baseY));
            geometry.LineTo(new Point(cx + head, baseY));
            geometry.LineTo(new Point(cx, tipY));
            geometry.LineTo(new Point(cx - head, baseY));
            geometry.LineTo(new Point(cx - shaft, baseY));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(brush, null, arrow);
    }

    // MARK: - Pill

    private static void DrawPill(DrawingContext context, double s, double? fill, Color accent)
    {
        var width = PillWidth * s;
        var height = PillHeight * s;
        var x = 0.5 * s - width / 2;
        var y = PillY * s;
        var radius = height / 2;

        context.DrawRectangle(new ImmutableSolidColorBrush(accent, TrackAlpha), null,
            new RoundedRect(new Rect(x, y, width, height), radius));

        if (fill is { } fraction)
        {
            context.DrawRectangle(new ImmutableSolidColorBrush(accent), null,
                new RoundedRect(new Rect(x, y, width * fraction, height), radius));
        }
    }
}
