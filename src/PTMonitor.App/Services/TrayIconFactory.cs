using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace PTMonitor.App.Services;

/// <summary>
/// Renders the tray icons at runtime with vector drawing, so the app ships
/// without binary icon assets. All three states share one geometry ported
/// from the macOS app-icon generator: a charcoal rounded card with a
/// hairline border, a down/up arrow pair and a pill progress meter. The
/// states differ only in colour: idle dims both arrows and shows only the
/// pill track; active is the app icon proper (solid teal down arrow, pill
/// 62% filled); error swaps teal for red, fills the pill and outlines the
/// card with a red stroke.
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

    public static WindowIcon Idle() => new(Render(TrayState.Idle));
    public static WindowIcon Active() => new(Render(TrayState.Active));
    public static WindowIcon Error() => new(Render(TrayState.Error));

    /// <summary>Renders the shared card/arrows/pill artwork for one state.</summary>
    public static RenderTargetBitmap Render(TrayState state)
    {
        var s = (double)Size;
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            DrawCard(context, s, isError: state == TrayState.Error);

            var teal = state == TrayState.Error;
            var solid = state != TrayState.Idle;
            double downAlpha = solid ? 1.0 : 0.38;

            // Down arrow: gradient, lighter colour at the wide (head) end.
            var downTop = teal ? Color.Parse("#C33C39") : Color.Parse("#3AA7B5");
            var downHead = teal ? Color.Parse("#E5484D") : Color.Parse("#6FD3DE");
            var downBrush = Gradient(0.66 * s, 0.32 * s, downHead, downTop, downAlpha);
            DrawArrow(context, s, down: true, downBrush);

            // Up arrow: solid, dimmed in every state.
            var upColor = teal ? Color.Parse("#C33C39") : Color.Parse("#4FB6C3");
            DrawArrow(context, s, down: false, new ImmutableSolidColorBrush(WithAlpha(upColor, 0.38)));

            // Pill fill: none when idle, 62% teal when active, full red on error.
            double? fill = state switch
            {
                TrayState.Active => 0.62,
                TrayState.Error => 1.0,
                _ => null,
            };
            var fillBrush = fill is null ? null
                : new ImmutableSolidColorBrush(teal ? Color.Parse("#E5484D") : Color.Parse("#4FB6C3"));
            DrawPill(context, s, fill, fillBrush);
        }
        return bitmap;
    }

    // MARK: - Card

    private static void DrawCard(DrawingContext context, double s, bool isError)
    {
        var radius = 0.225 * s;
        var rect = new Rect(0, 0, s, s);

        var card = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#2E2E33"), 0),
                new GradientStop(Color.Parse("#131316"), 1),
            },
        }.ToImmutable();
        context.DrawRectangle(card, null, new RoundedRect(rect, radius));

        // Sheen: white fading out over the top 58% of the card.
        var sheen = Gradient(0, 0.58 * s, Color.Parse("#FFFFFF"), Color.Parse("#FFFFFF"), 0.07,
            endAlpha: 0.0);
        using (context.PushGeometryClip(new RectangleGeometry(rect, radius, radius)))
        {
            context.DrawRectangle(sheen, null, new Rect(0, 0, s, 0.58 * s));
        }

        // Border: hairline white, or a legible red stroke on error.
        IImmutableBrush border = new ImmutableSolidColorBrush(WithAlpha(Color.Parse("#FFFFFF"), 0.10));
        double thickness = 0.004 * s;
        if (isError)
        {
            border = new ImmutableSolidColorBrush(WithAlpha(Color.Parse("#E5484D"), 0.55));
            thickness = 0.05 * s;
        }
        context.DrawRectangle(null, new ImmutablePen(border, thickness),
            new RoundedRect(rect, radius));
    }

    // MARK: - Arrows

    private static void DrawArrow(DrawingContext context, double s, bool down, IBrush brush)
    {
        double bandTop = 0.32 * s;
        double bandBottom = 0.66 * s;
        double cx = (down ? 0.37 : 0.63) * s;
        double shaftHalf = 0.043 * s;
        double headHalf = 0.105 * s;
        double headDepth = 0.105 * s;

        // Tip at the arrow's pointing end; the shaft starts at the opposite end.
        double tipY = down ? bandBottom : bandTop;
        double baseY = down ? bandBottom - headDepth : bandTop + headDepth;
        double farY = down ? bandTop : bandBottom;

        var arrow = new StreamGeometry();
        using (var geometry = arrow.Open())
        {
            geometry.BeginFigure(new Point(cx - shaftHalf, farY), true);
            geometry.LineTo(new Point(cx + shaftHalf, farY));
            geometry.LineTo(new Point(cx + shaftHalf, baseY));
            geometry.LineTo(new Point(cx + headHalf, baseY));
            geometry.LineTo(new Point(cx, tipY));
            geometry.LineTo(new Point(cx - headHalf, baseY));
            geometry.LineTo(new Point(cx - shaftHalf, baseY));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(brush, null, arrow);
    }

    // MARK: - Pill

    private static void DrawPill(DrawingContext context, double s, double? fill, IBrush? fillBrush)
    {
        double width = 0.52 * s;
        double height = 0.032 * s;
        double x = 0.5 * s - width / 2;
        double y = 0.79 * s;

        var track = new ImmutableSolidColorBrush(WithAlpha(Color.Parse("#4FB6C3"), 0.12));
        context.DrawRectangle(track, null, new RoundedRect(new Rect(x, y, width, height), height / 2));

        if (fill is { } fraction && fillBrush is not null)
        {
            context.DrawRectangle(fillBrush, null,
                new RoundedRect(new Rect(x, y, width * fraction, height), height / 2));
        }
    }

    // MARK: - Colour helpers

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(byte.MaxValue * alpha), color.R, color.G, color.B);

    /// <summary>A vertical absolute-coordinate gradient; both stops share
    /// <paramref name="alpha"/> unless <paramref name="endAlpha"/> is given.</summary>
    private static IBrush Gradient(double y0, double y1, Color top, Color bottom,
        double alpha = 1.0, double? endAlpha = null)
    {
        double a1 = endAlpha ?? alpha;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, y0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, y1, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(WithAlpha(top, alpha), 0),
                new GradientStop(WithAlpha(bottom, a1), 1),
            },
        }.ToImmutable();
    }
}
