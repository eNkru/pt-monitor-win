namespace PTMonitor.Core.Model;

/// <summary>
/// How much a poll has to fetch. <see cref="Summary"/> carries the aggregate
/// rates, the torrent counts, the connection state and the free space — the
/// things the tray tooltip and the floating chip show. <see cref="Full"/>
/// adds the torrent list, which only the panel displays.
/// </summary>
public enum PollDetail
{
    /// <summary>Rates, counts, connection state, free space. No torrent list.</summary>
    Summary,

    /// <summary>Everything, including the torrent list.</summary>
    Full,
}
