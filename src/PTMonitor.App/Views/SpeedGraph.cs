using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PTMonitor.App.Views;

/// <summary>
/// Rolling two-series speed graph, drawn at runtime (no assets): download in
/// green, upload in orange, overlaid on one plot with the newest sample at the
/// right. Both series share the peak of the visible window so their heights
/// are directly comparable.
/// </summary>
public sealed class SpeedGraph : Control
{
    /// <summary>Samples kept; at the one-per-second tick this is one minute.</summary>
    public const int Capacity = 60;

    private static readonly Color DownColor = Color.Parse("#35C46F");
    private static readonly Color UpColor = Color.Parse("#F2994A");

    private readonly long[] _down = new long[Capacity];
    private readonly long[] _up = new long[Capacity];
    private int _start;
    private int _count;

    /// <summary>Appends one aggregate sample and repaints.</summary>
    public void Push(long down, long up)
    {
        down = Math.Max(0, down);
        up = Math.Max(0, up);
        if (_count < Capacity)
        {
            var index = (_start + _count) % Capacity;
            _down[index] = down;
            _up[index] = up;
            _count++;
        }
        else
        {
            _down[_start] = down;
            _up[_start] = up;
            _start = (_start + 1) % Capacity;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 4 || height <= 4 || _count == 0) return;

        long peak = 1;
        for (var i = 0; i < _count; i++)
        {
            peak = Math.Max(peak, Math.Max(_down[(_start + i) % Capacity], _up[(_start + i) % Capacity]));
        }

        RenderSeries(context, _down, DownColor, width, height, peak);
        RenderSeries(context, _up, UpColor, width, height, peak);
    }

    private void RenderSeries(
        DrawingContext context, long[] values, Color color, double width, double height, long peak)
    {
        var step = width / (Capacity - 1);
        var bottom = height - 1;
        var usable = height - 2;

        Point PointAt(int i)
        {
            var rate = Math.Max(0, values[(_start + i) % Capacity]);
            var x = width - (_count - 1 - i) * step;
            var y = bottom - (double)rate / peak * usable;
            return new Point(x, y);
        }

        var fill = new StreamGeometry();
        using (var geometry = fill.Open())
        {
            geometry.BeginFigure(new Point(PointAt(0).X, bottom), true);
            for (var i = 0; i < _count; i++) geometry.LineTo(PointAt(i));
            geometry.LineTo(new Point(PointAt(_count - 1).X, bottom));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(0x4D, color.R, color.G, color.B)), null, fill);

        if (_count < 2) return;

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(PointAt(0), false);
            for (var i = 1; i < _count; i++) geometry.LineTo(PointAt(i));
            geometry.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.4), line);
    }
}
