using System.Globalization;

namespace PTMonitor.Core.Format;

public enum MenuBarTextForm
{
    Full,
    Compact,
}

/// <summary>Byte-rate formatting and the tray text degradation ladder.</summary>
public static class ByteRateFormatter
{
    public const string DownArrow = "↓";
    public const string UpArrow = "↑";

    private const double BinaryStep = 1024.0;
    private static readonly string[] Units = ["K", "M", "G", "T", "P", "E"];

    public static string Rate(long bytesPerSecond)
    {
        var bytes = Math.Max(0, bytesPerSecond);
        if (bytes < BinaryStep) return $"{bytes} B/s";

        var (value, unitIndex) = Scaled(bytes);
        var number = value < 100
            ? Math.Round(value, 1, MidpointRounding.ToEven).ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(value, 0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
        return number + Units[unitIndex];
    }

    public static string Bytes(long count)
    {
        var bytes = Math.Max(0, count);
        if (bytes < BinaryStep) return $"{bytes} B";

        var (value, unitIndex) = Scaled(bytes);
        var unit = Units[unitIndex];
        // printf-style rounding (half to even), matching the macOS original.
        if (value < 10) return $"{Math.Round(value, 2, MidpointRounding.ToEven).ToString("0.00", CultureInfo.InvariantCulture)} {unit}B";
        if (value < 100) return $"{Math.Round(value, 1, MidpointRounding.ToEven).ToString("0.0", CultureInfo.InvariantCulture)} {unit}B";
        return $"{Math.Round(value, 0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture)} {unit}B";
    }

    private static (double Value, int UnitIndex) Scaled(long bytes)
    {
        var value = (double)bytes;
        var unitIndex = -1;
        while (value >= BinaryStep && unitIndex < Units.Length - 1)
        {
            value /= BinaryStep;
            unitIndex++;
        }
        return (value, unitIndex);
    }

    public static string SpeedText(long down, long up, MenuBarTextForm form = MenuBarTextForm.Full)
    {
        var gap = form == MenuBarTextForm.Full ? " " : "";
        return $"{DownArrow}{gap}{Rate(down)} {UpArrow}{gap}{Rate(up)}";
    }

    /// <summary>Full → compact → null (icon only). The measurer is injected.</summary>
    public static string? DegradedSpeedText(long down, long up, double availableWidth, Func<string, double> measure)
    {
        var full = SpeedText(down, up);
        if (measure(full) <= availableWidth) return full;
        var compact = SpeedText(down, up, MenuBarTextForm.Compact);
        if (measure(compact) <= availableWidth) return compact;
        return null;
    }
}
