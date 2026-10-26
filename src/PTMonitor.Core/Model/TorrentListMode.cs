namespace PTMonitor.Core.Model;

/// <summary>Which slice of a server's torrent library the panel lists.</summary>
public enum TorrentListMode
{
    All,
    Downloading,
    Uploading,
    Active,
}

public static class TorrentListModeExtensions
{
    public static bool Matches(this TorrentListMode mode, TorrentSummary torrent) => mode switch
    {
        TorrentListMode.All => true,
        TorrentListMode.Downloading => torrent.DownSpeed > 0,
        TorrentListMode.Uploading => torrent.UpSpeed > 0,
        TorrentListMode.Active => torrent.IsActive,
        _ => true,
    };

    public static List<TorrentSummary> Filter(this TorrentListMode mode, IEnumerable<TorrentSummary> torrents) =>
        torrents.Where(torrent => mode.Matches(torrent)).ToList();
}
