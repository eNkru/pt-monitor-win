using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace PTMonitor.App.Services;

/// <summary>
/// Renders the tray icons at runtime with vector drawing, so the app ships
/// without binary icon assets. The tray icon shows the worst state among the
/// enabled servers (idle circle / active circle / warning triangle).
/// </summary>
public static class TrayIconFactory
{
    private const int Size = 32;

    public static WindowIcon Idle() => RenderCircle(Color.Parse("#6E6E73"));
    public static WindowIcon Active() => RenderCircle(Color.Parse("#2D7D46"));
    public static WindowIcon Error() => RenderWarning(Color.Parse("#C33C39"));

    private static WindowIcon RenderCircle(Color color)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var center = new Rect(2, 2, Size - 4, Size - 4);
            context.DrawEllipse(new ImmutableSolidColorBrush(color), null, center);

            // Down-arrow: stem + head, in white.
            var arrow = new StreamGeometry();
            using (var geometry = arrow.Open())
            {
                geometry.BeginFigure(new Point(14, 9), true);
                geometry.LineTo(new Point(18, 9));
                geometry.LineTo(new Point(18, 16));
                geometry.LineTo(new Point(23, 16));
                geometry.LineTo(new Point(16, 24));
                geometry.LineTo(new Point(9, 16));
                geometry.LineTo(new Point(14, 16));
                geometry.EndFigure(true);
            }
            context.DrawGeometry(Brushes.White, null, arrow);
        }
        return new WindowIcon(bitmap);
    }

    private static WindowIcon RenderWarning(Color color)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            // Warning triangle: apex at top-center.
            var triangle = new StreamGeometry();
            using (var geometry = triangle.Open())
            {
                geometry.BeginFigure(new Point(16, 3), true);
                geometry.LineTo(new Point(29, 27));
                geometry.LineTo(new Point(3, 27));
                geometry.EndFigure(true);
            }
            context.DrawGeometry(new ImmutableSolidColorBrush(color), null, triangle);

            // Exclamation: bar and dot, in white.
            context.DrawRectangle(Brushes.White, null, new Rect(14.5f, 11, 3, 9), 1.5, 1.5);
            context.DrawRectangle(Brushes.White, null, new Rect(14.5f, 22.5, 3, 3), 1.5, 1.5);
        }
        return new WindowIcon(bitmap);
    }
}
