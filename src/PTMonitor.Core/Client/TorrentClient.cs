using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

/// <summary>The two APIs normalise into one domain model behind this interface.</summary>
public interface ITorrentClient : IAsyncDisposable
{
    /// <summary>
    /// Fetches the server's state. <paramref name="detail"/> says how much the
    /// caller needs: <see cref="PollDetail.Summary"/> is the aggregate state
    /// without the torrent list, <see cref="PollDetail.Full"/> adds it. The
    /// client picks the cheapest request that satisfies the scope.
    /// </summary>
    Task<TransferSnapshot> FetchSnapshotAsync(PollDetail detail, CancellationToken cancellationToken);

    Task<string> TestConnectionAsync(CancellationToken cancellationToken);
}
