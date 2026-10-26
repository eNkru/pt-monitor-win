using System.Text.Json.Serialization;

namespace PTMonitor.Core.Model;

/// <summary>Normalised per-torrent state. Both APIs map onto this.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TorrentState>))]
public enum TorrentState
{
    Downloading,
    Seeding,
    Stalled,
    Queued,
    Checking,
    Paused,
    Error,
    Other,
}

public enum TrackerStatus
{
    Working,
    NotWorking,
}

/// <summary>One line of a torrent row's expanded detail block, in display order.</summary>
public abstract record TorrentDetailLine
{
    public sealed record SizeLine(long Total, long? Downloaded, double Progress) : TorrentDetailLine;
    public sealed record RatioLine(double Ratio, long? Uploaded, long? Downloaded) : TorrentDetailLine;
    public sealed record PeersLine(int? Seeders, int? Leechers) : TorrentDetailLine;
    public sealed record TrackerLine(TrackerStatus Status) : TorrentDetailLine;
    public sealed record CategoryLine(string Value) : TorrentDetailLine;
    public sealed record ErrorLine(string Value) : TorrentDetailLine;
}

/// <summary>One piece of a torrent row's detail line.</summary>
public abstract record TorrentRowInfoSegment
{
    public sealed record Down(long Rate) : TorrentRowInfoSegment;
    public sealed record Up(long Rate) : TorrentRowInfoSegment;
    public sealed record Eta(long Seconds) : TorrentRowInfoSegment;
    public sealed record StateWord(TorrentState State) : TorrentRowInfoSegment;
}

/// <summary>Normalised per-torrent summary produced by both clients.</summary>
public sealed class TorrentSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public TorrentState State { get; init; }
    public double Progress { get; init; }
    public long DownSpeed { get; init; }
    public long UpSpeed { get; init; }
    public long? EtaSeconds { get; init; }

    public long? TotalSize { get; init; }
    public long? Downloaded { get; init; }
    public long? Uploaded { get; init; }
    public double? Ratio { get; init; }
    public int? Seeders { get; init; }
    public int? Leechers { get; init; }
    public string? Site { get; init; }
    public TrackerStatus? TrackerStatus { get; init; }
    public string? Category { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsActive => DownSpeed > 0 || UpSpeed > 0;

    /// <summary>Fixed display order: downloading, seeding, rest; then speed desc, name.</summary>
    public static List<TorrentSummary> Sorted(IEnumerable<TorrentSummary> summaries)
    {
        static int Rank(TorrentState state) => state switch
        {
            TorrentState.Downloading => 0,
            TorrentState.Seeding => 1,
            _ => 2,
        };

        return summaries
            .OrderBy(t => Rank(t.State))
            .ThenByDescending(t => t.DownSpeed)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static double ClampedProgress(double? raw)
    {
        if (raw is null || double.IsNaN(raw.Value) || double.IsInfinity(raw.Value)) return 0;
        return Math.Clamp(raw.Value, 0, 1);
    }

    public static string PercentText(double progress) =>
        $"{Math.Round(ClampedProgress(progress) * 100)}%";

    /// <summary>Which detail segments a row shows; never blank.</summary>
    public static List<TorrentRowInfoSegment> InfoSegments(TorrentState state, long down, long up, long? etaSeconds)
    {
        var segments = new List<TorrentRowInfoSegment>();
        switch (state)
        {
            case TorrentState.Downloading:
                if (down > 0) segments.Add(new TorrentRowInfoSegment.Down(down));
                if (up > 0) segments.Add(new TorrentRowInfoSegment.Up(up));
                if (etaSeconds is >= 0) segments.Add(new TorrentRowInfoSegment.Eta(etaSeconds.Value));
                break;
            case TorrentState.Seeding:
                if (up > 0) segments.Add(new TorrentRowInfoSegment.Up(up));
                break;
        }
        if (segments.Count == 0) segments.Add(new TorrentRowInfoSegment.StateWord(state));
        return segments;
    }

    /// <summary>The PT site from the first announce URL, or null.</summary>
    public static string? SiteFromAnnounceUrl(string? url)
    {
        var host = url?.Trim();
        if (string.IsNullOrEmpty(host)) return null;

        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];

        var terminator = host.IndexOfAny(['/', '?', '#', ':']);
        if (terminator >= 0) host = host[..terminator];

        host = host.ToLowerInvariant();

        string[] prefixes = ["tracker.", "announce.", "www.", "tr.", "t."];
        var stripped = 0;
        while (stripped < 2)
        {
            var prefix = prefixes.FirstOrDefault(host.StartsWith);
            if (prefix is null) break;
            host = host[prefix.Length..];
            stripped++;
        }

        return string.IsNullOrEmpty(host) ? null : host;
    }

    /// <summary>The site shared by every row, or null when it must be per-row.</summary>
    public static string? HoistedSite(IReadOnlyList<TorrentSummary> torrents)
    {
        if (torrents.Count == 0) return null;
        var first = torrents[0].Site;
        if (first is null) return null;
        foreach (var torrent in torrents)
        {
            if (torrent.Site != first) return null;
        }
        return first;
    }

    public static List<TorrentDetailLine> ExpandedLines(TorrentSummary torrent)
    {
        var lines = new List<TorrentDetailLine>();
        if (torrent.TotalSize is { } total)
            lines.Add(new TorrentDetailLine.SizeLine(total, torrent.Downloaded, torrent.Progress));
        if (torrent.Ratio is { } ratio)
            lines.Add(new TorrentDetailLine.RatioLine(ratio, torrent.Uploaded, torrent.Downloaded));
        if (torrent.Seeders is not null || torrent.Leechers is not null)
            lines.Add(new TorrentDetailLine.PeersLine(torrent.Seeders, torrent.Leechers));
        if (torrent.TrackerStatus is { } status)
            lines.Add(new TorrentDetailLine.TrackerLine(status));
        if (torrent.Category is { } category)
            lines.Add(new TorrentDetailLine.CategoryLine(category));
        if (torrent.ErrorMessage is { } error)
            lines.Add(new TorrentDetailLine.ErrorLine(error));
        return lines;
    }
}
