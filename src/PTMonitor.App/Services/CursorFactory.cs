using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace PTMonitor.App.Services;

/// <summary>
/// Draws the floating chip's cursor at runtime, like <see cref="TrayIconFactory"/>
/// draws the tray icon — no binary cursor asset. Avalonia's
/// <see cref="StandardCursorType.Hand"/> is a pointing finger; this is the open
/// hand macOS uses for a draggable surface: white body, dark outline, fingers
/// fanned out and the whole hand leaning slightly.
/// </summary>
public static class CursorFactory
{
    private const int Size = 32;

    // White body with a dark outline, so it reads on light and dark desktops.
    private static readonly Color Body = Colors.White;
    private static readonly Color Outline = Color.Parse("#1A1A1A");

    public static Cursor OpenHand()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            DrawHand(context);
        }
        // Hot spot on the index fingertip, where a click would land.
        return new Cursor(bitmap, new PixelPoint(8, 7));
    }

    private static void DrawHand(DrawingContext context)
    {
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Outline), 1.3,
            lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        var fill = new ImmutableSolidColorBrush(Body);

        // Slight overall tilt, like the macOS open-hand cursor.
        const double pi = Math.PI;
        var tilt = Matrix.CreateTranslation(-14, -16)
                   * Matrix.CreateRotation(-6 * pi / 180)
                   * Matrix.CreateTranslation(14, 16);
        using (context.PushTransform(tilt))
        {
            // Fingers, fanned outward; they run into the palm, which is painted
            // afterwards so the joins are hidden.
            var fingers = new (double X, double Top, double Angle)[]
            {
                (7.4, 6.0, -8.0),
                (11.2, 3.6, -2.5),
                (15.0, 4.2, 2.5),
                (18.6, 7.2, 8.0),
            };
            foreach (var finger in fingers)
                DrawCapsule(context, fill, pen, finger.X, 18.0, 4.0, 18.0 - finger.Top, finger.Angle);

            // Thumb, angled up-left out of the palm.
            DrawCapsule(context, fill, pen, 7.6, 22.0, 4.0, 8.0, -38);

            // Palm: filled (no stroke) so it hides the finger joins, then the
            // outline is stroked as an open path that skips the top edge —
            // otherwise a straight line cuts across the base of the fingers.
            var palm = new Rect(5.0, 15.0, 17.0, 13.0);
            context.DrawRectangle(fill, null,
                new RoundedRect(palm, new CornerRadius(3.0, 3.0, 7.0, 7.0)));

            var outline = new StreamGeometry();
            using (var geometry = outline.Open())
            {
                geometry.BeginFigure(new Point(palm.Left, palm.Top), false);
                geometry.LineTo(new Point(palm.Left, palm.Bottom - 7));
                geometry.ArcTo(new Point(palm.Left + 7, palm.Bottom), new Size(7, 7), 0,
                    false, SweepDirection.CounterClockwise);
                geometry.LineTo(new Point(palm.Right - 7, palm.Bottom));
                geometry.ArcTo(new Point(palm.Right, palm.Bottom - 7), new Size(7, 7), 0,
                    false, SweepDirection.CounterClockwise);
                geometry.LineTo(new Point(palm.Right, palm.Top));
                geometry.EndFigure(false);
            }
            context.DrawGeometry(null, pen, outline);
        }
    }

    private static void DrawCapsule(
        DrawingContext context, IBrush fill, IPen pen,
        double baseX, double baseY, double width, double height, double angleDeg)
    {
        var rotation = Matrix.CreateTranslation(-baseX, -baseY)
                       * Matrix.CreateRotation(angleDeg * Math.PI / 180)
                       * Matrix.CreateTranslation(baseX, baseY);
        using (context.PushTransform(rotation))
        {
            context.DrawRectangle(fill, pen,
                new RoundedRect(new Rect(baseX - width / 2, baseY - height, width, height + 5), width / 2));
        }
    }
}
