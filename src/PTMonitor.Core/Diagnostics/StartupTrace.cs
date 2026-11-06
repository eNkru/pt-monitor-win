using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PTMonitor.Core.Diagnostics;

/// <summary>One launch phase and its cumulative offset from <see cref="StartupTrace.Begin"/>, in milliseconds.</summary>
public readonly record struct StartupMark(string Phase, double Milliseconds);

/// <summary>
/// The launch timeline: records how long the path from process entry to a usable
/// tray icon took, and writes it as one <c>startup</c> line so a user can paste
/// it into a performance report.
///
/// No lock. Every mark runs on the UI thread — <c>Program.Main</c> → <c>App</c> →
/// the <c>AppServices</c> constructor → the posted window callback — so the mark
/// list is only ever touched from one thread.
///
/// Cheap by construction: a mark is one clock read plus one list add, nothing
/// else. There is no I/O until <see cref="Flush"/>, and <see cref="Begin"/> never
/// ran makes <see cref="Mark"/> and <see cref="Flush"/> no-ops, which keeps the
/// CLI, the selftest and the xUnit suite free of file side effects — the same
/// contract <see cref="Log"/> has.
/// </summary>
public static class StartupTrace
{
    /// <summary>
    /// The phases in causal order — the same order the launch path runs, so the
    /// rendered offsets are non-decreasing. Fixed constants so the line carries
    /// nothing that needs redacting, and so two runs diff cleanly. Never sorted —
    /// the order <em>is</em> the causal chain.
    /// </summary>
    private static readonly string[] PhaseOrder =
        ["entry", "log-ready", "avalonia", "glyph", "tray-set", "stores", "windows"];

    private static List<StartupMark>? _marks;
    private static long _start;

    private static Func<double> _clock = DefaultElapsed;

    /// <summary>
    /// Milliseconds since <see cref="Begin"/>. Replaceable so tests can drive a
    /// deterministic counter; <see cref="Reset"/> restores the stopwatch.
    /// Internal: only the in-binary selftest and the xUnit suite set it (via
    /// <c>InternalsVisibleTo</c>), so shipped code cannot swap the clock out
    /// from under the timeline.
    /// </summary>
    internal static Func<double> Clock
    {
        get => _clock;
        set => _clock = value;
    }

    /// <summary>
    /// Starts the timeline. Called as the first statement of the process entry
    /// point, so <c>entry = 0 ms</c> and every offset is measured from the
    /// earliest point a managed app can observe.
    /// </summary>
    public static void Begin()
    {
        _marks = [new StartupMark("entry", 0)];
        _start = Stopwatch.GetTimestamp();
    }

    /// <summary>Records a phase. A no-op when <see cref="Begin"/> never ran.</summary>
    public static void Mark(string phase)
    {
        if (_marks is null) return;
        _marks.Add(new StartupMark(phase, _clock()));
    }

    /// <summary>
    /// Writes the timeline as one line and clears it, so a second call is a
    /// no-op. One line per launch, not one per phase: the sink's rule is one
    /// line per event.
    /// </summary>
    public static void Flush()
    {
        if (_marks is not { } marks) return;
        _marks = null;
        // The last offset is the total; max guards a clock that is not strictly
        // increasing, so the total can never understate the launch.
        var total = marks.Count == 0 ? 0 : marks.Max(mark => mark.Milliseconds);
        Log.Info("startup", $"timeline {Format(marks, total)}");
    }

    /// <summary>
    /// Renders the phases in the fixed causal order — entry first, then each
    /// known phase that was marked, then anything unexpected in the order it was
    /// added — ending in the total. Pure, so the selftest and the suite can pin
    /// it without a clock. An empty list renders a zero timeline instead of
    /// throwing.
    /// </summary>
    public static string Format(IReadOnlyList<StartupMark> marks, double totalMs)
    {
        var builder = new StringBuilder();
        if (marks.Count == 0)
        {
            builder.Append("entry=").Append(OneDecimal(0)).Append(' ');
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var phase in PhaseOrder)
            {
                foreach (var mark in marks)
                {
                    if (string.Equals(mark.Phase, phase, StringComparison.Ordinal) && seen.Add(mark.Phase))
                        Append(builder, mark);
                }
            }
            foreach (var mark in marks)
            {
                if (seen.Add(mark.Phase)) Append(builder, mark);
            }
        }

        return builder.Append("total=").Append(OneDecimal(totalMs)).Append(" ms").ToString();
    }

    /// <summary>Clears the timeline and restores the stopwatch clock (tests).</summary>
    public static void Reset()
    {
        _marks = null;
        _start = 0;
        _clock = DefaultElapsed;
    }

    private static void Append(StringBuilder builder, StartupMark mark) =>
        builder.Append(mark.Phase).Append('=').Append(OneDecimal(mark.Milliseconds)).Append(' ');

    private static string OneDecimal(double milliseconds) =>
        milliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static double DefaultElapsed() =>
        _start == 0 ? 0 : (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
}
