using PTMonitor.Core.Model;

namespace PTMonitor.Core.Client;

/// <summary>The two APIs normalise into one domain model behind this interface.</summary>
public interface ITorrentClient : IAsyncDisposable
{
    Task<TransferSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken);
    Task<string> TestConnectionAsync(CancellationToken cancellationToken);
}
