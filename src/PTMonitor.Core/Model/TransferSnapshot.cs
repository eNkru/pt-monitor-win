using System.Text.Json.Serialization;

namespace PTMonitor.Core.Model;

public enum ConnectionState
{
    Connected,
    Firewalled,
    Disconnected,
    Unknown,
}

/// <summary>One cross-client snapshot of a server's transfer state.</summary>
public sealed class TransferSnapshot
{
    public long DownSpeed { get; init; }
    public long UpSpeed { get; init; }
    public int ActiveTorrents { get; init; }
    public int TotalTorrents { get; init; }
    public long? FreeSpace { get; set; }
    public ConnectionState ConnectionState { get; init; } = ConnectionState.Unknown;
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
    public List<TorrentSummary> Torrents { get; set; } = [];

    /// <summary>
    /// What this snapshot carries: <see cref="PollDetail.Summary"/> means the
    /// torrent list is not part of it (the counts are), <see cref="PollDetail.Full"/>
    /// means the list is there. The store keeps the last full list, so a
    /// summary snapshot never erases what the panel shows.
    /// </summary>
    public PollDetail Detail { get; init; } = PollDetail.Full;
}

public enum AppLanguage
{
    System,
    English,
    Chinese,
}

public static class AppLanguageExtensions
{
    public static string RawValue(this AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        AppLanguage.Chinese => "zh-Hans",
        _ => "system",
    };

    public static bool TryParse(string? raw, out AppLanguage language)
    {
        switch (raw)
        {
            case "en":
                language = AppLanguage.English;
                return true;
            case "zh-Hans":
                language = AppLanguage.Chinese;
                return true;
            case "system":
            case null:
            case "":
                language = AppLanguage.System;
                return true;
            default:
                language = AppLanguage.System;
                return false;
        }
    }
}

public enum MenuBarState
{
    Idle,
    Active,
    Error,
}
