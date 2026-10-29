using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using PTMonitor.Core.Format;
using PTMonitor.Core.Model;
using PTMonitor.Core.Store;
using PTMonitor.App.Services;

namespace PTMonitor.App.ViewModels;

public sealed class DetailCellViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Note { get; init; }
}

public sealed class TorrentRowViewModel : INotifyPropertyChanged
{
    public required TorrentSummary Torrent { get; init; }
    public required ServerConfig Server { get; init; }
    public required string Name { get; init; }
    public required double Progress { get; init; }
    public required string PercentText { get; init; }
    public required string InfoText { get; init; }
    public required IBrush StateBrush { get; init; }
    public string? SiteText { get; init; }
    public required string WebUiToolTip { get; init; }
    public required Uri WebUiUri { get; init; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailCells)));
        }
    }

    public IReadOnlyList<DetailCellViewModel>? DetailCells
    {
        get
        {
            if (!IsExpanded) return null;
            var cells = new List<DetailCellViewModel>();
            foreach (var line in TorrentSummary.ExpandedLines(Torrent))
            {
                switch (line)
                {
                    case TorrentDetailLine.SizeLine size:
                        cells.Add(new DetailCellViewModel
                        {
                            Label = L10n.S("Size"),
                            Value = ByteRateFormatter.Bytes(size.Total),
                            Note = size.Downloaded is { } downloaded
                                ? $"{ByteRateFormatter.DownArrow} {ByteRateFormatter.Bytes(downloaded)}"
                                : null,
                        });
                        break;
                    case TorrentDetailLine.RatioLine ratio:
                        cells.Add(new DetailCellViewModel
                        {
                            Label = L10n.S("Ratio"),
                            Value = $"×{ratio.Ratio:0.00}",
                            Note = ratio.Uploaded is { } uploaded && ratio.Downloaded is { } downloadedTotal
                                ? $"{ByteRateFormatter.UpArrow} {ByteRateFormatter.Bytes(uploaded)} · {ByteRateFormatter.DownArrow} {ByteRateFormatter.Bytes(downloadedTotal)}"
                                : null,
                        });
                        break;
                    case TorrentDetailLine.PeersLine peers:
                        cells.Add(new DetailCellViewModel
                        {
                            Label = L10n.S("Seeds / Leechers"),
                            Value = $"{peers.Seeders?.ToString() ?? "—"} / {peers.Leechers?.ToString() ?? "—"}",
                        });
                        break;
                    case TorrentDetailLine.TrackerLine tracker:
                        cells.Add(new DetailCellViewModel
                        {
                            Label = L10n.S("Tracker"),
                            Value = tracker.Status == TrackerStatus.Working
                                ? L10n.S("Working")
                                : L10n.S("Not working"),
                        });
                        break;
                    case TorrentDetailLine.CategoryLine category:
                        cells.Add(new DetailCellViewModel { Label = L10n.S("Category"), Value = category.Value });
                        break;
                    case TorrentDetailLine.ErrorLine error:
                        cells.Add(new DetailCellViewModel { Label = L10n.S("Error"), Value = error.Value });
                        break;
                }
            }
            return cells;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ServerSectionViewModel
{
    public required ServerConfig Server { get; init; }
    public required string Title { get; init; }
    public string? SpeedText { get; init; }
    public string? HoistedSite { get; init; }
    public required ServerStatus Status { get; init; }
    public required IReadOnlyList<TorrentRowViewModel> Rows { get; init; }
    public string? MoreText { get; init; }
    public string? NoteText { get; init; }
    public string? FailureHeadline { get; init; }
    public string? FailureDetail { get; init; }
}

public sealed class PanelViewModel : INotifyPropertyChanged
{
    public const int MaxVisibleTorrentsPerServer = 100;

    private readonly SettingsStore _settings;
    private readonly MonitorStore _monitor;
    private readonly ObservableCollection<ServerSectionViewModel> _sections = [];
    private string? _expandedTorrentId;

    public PanelViewModel(SettingsStore settings, MonitorStore monitor)
    {
        _settings = settings;
        _monitor = monitor;
        // StatusesChanged is raised from the poll loop's thread-pool thread;
        // Rebuild mutates the bound Sections collection and must run on the UI
        // thread. Settings.Changed is always raised on the UI thread already.
        monitor.StatusesChanged += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        settings.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    public IReadOnlyList<ServerSectionViewModel> Sections => _sections;

    public string HeaderFreshness { get; private set; } = "";
    public string HeaderServerCount { get; private set; } = "";
    public string DownSpeedText { get; private set; } = "—";
    public string UpSpeedText { get; private set; } = "—";
    public string ActiveTorrentsText { get; private set; } = "—";
    public string FreeSpaceText { get; private set; } = "—";
    public bool IsEmptyState { get; private set; }
    public IReadOnlyList<TorrentListMode> Modes => [.. Enum.GetValues<TorrentListMode>()];
    public TorrentListMode SelectedMode => _settings.TorrentListMode;

    public void RefreshFreshness()
    {
        HeaderFreshness = L10n.Freshness(NewestFetchDate(), DateTimeOffset.Now);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderFreshness)));
    }

    private DateTimeOffset? NewestFetchDate()
    {
        DateTimeOffset? newest = null;
        foreach (var status in _monitor.Statuses.Values)
        {
            if (status.Snapshot?.FetchedAt is not { } fetched) continue;
            if (newest is null || fetched > newest) newest = fetched;
        }
        return newest;
    }

    public void SelectMode(TorrentListMode mode)
    {
        _settings.SetTorrentListMode(mode);
        // Display-only (R12): filtering the list never rebuilds the pollers.
        Rebuild();
    }

    public void ToggleExpanded(TorrentRowViewModel row)
    {
        _expandedTorrentId = _expandedTorrentId == row.Torrent.Id ? null : row.Torrent.Id;
        Rebuild();
    }

    public void Rebuild()
    {
        var statuses = _monitor.Statuses;
        var servers = _settings.Servers.Where(server => server.IsEnabled).ToList();
        var mode = _settings.TorrentListMode;

        _sections.Clear();
        foreach (var server in servers)
        {
            var status = statuses.GetValueOrDefault(server.Id) ?? ServerStatus.Connecting.Instance;
            var library = status.Snapshot?.Torrents ?? [];
            var candidates = mode.Filter(library);
            var visible = candidates.Take(MaxVisibleTorrentsPerServer).ToList();
            var hoisted = TorrentSummary.HoistedSite(visible);

            var rows = visible.Select(torrent => new TorrentRowViewModel
            {
                Torrent = torrent,
                Server = server,
                Name = torrent.Name,
                Progress = torrent.Progress,
                PercentText = TorrentSummary.PercentText(torrent.Progress),
                InfoText = string.Join(" · ", TorrentSummary.InfoSegments(
                        torrent.State, torrent.DownSpeed, torrent.UpSpeed, torrent.EtaSeconds)
                    .Select(SegmentText)),
                StateBrush = StateBrush(torrent.State),
                SiteText = torrent.Site is not null && hoisted is null ? torrent.Site : null,
                WebUiToolTip = L10n.OpenInWebUi(server.DisplayName),
                WebUiUri = SafeWebUri(server),
                IsExpanded = _expandedTorrentId == torrent.Id,
            }).ToList();

            string? note = null;
            if (status is ServerStatus.Online)
            {
                note = candidates.Count == 0
                    ? (library.Count == 0 ? L10n.S("No torrents.") : L10n.NoTorrents(mode))
                    : null;
            }

            _sections.Add(new ServerSectionViewModel
            {
                Server = server,
                Title = server.DisplayName,
                SpeedText = status.Snapshot is { } snapshot
                    ? ByteRateFormatter.SpeedText(snapshot.DownSpeed, snapshot.UpSpeed)
                    : null,
                HoistedSite = hoisted,
                Status = status,
                Rows = rows,
                MoreText = candidates.Count > rows.Count
                    ? L10n.MoreTorrents(candidates.Count - rows.Count)
                    : null,
                NoteText = note,
                FailureHeadline = status.Failure is { } failure ? L10n.CauseHeadline(failure) : null,
                FailureDetail = status.Failure?.Message,
            });
        }

        var aggregate = _monitor.Aggregate;
        var anyOnline = aggregate.OnlineServers > 0;
        var totalTorrents = statuses.Values.Sum(status => status.Snapshot?.TotalTorrents ?? 0);
        var freeSpace = statuses.Values
            .Select(status => status.Snapshot?.FreeSpace)
            .Where(value => value is not null)
            .Min();

        DownSpeedText = anyOnline ? $"{ByteRateFormatter.DownArrow} {ByteRateFormatter.Rate(aggregate.DownSpeed)}" : "—";
        UpSpeedText = anyOnline ? $"{ByteRateFormatter.UpArrow} {ByteRateFormatter.Rate(aggregate.UpSpeed)}" : "—";
        ActiveTorrentsText = anyOnline ? L10n.ActiveTorrents(aggregate.ActiveTorrents, totalTorrents) : "—";
        FreeSpaceText = freeSpace is { } space ? ByteRateFormatter.Bytes(space) : "—";
        HeaderServerCount = L10n.ServerCount(_settings.Servers.Count, aggregate.OnlineServers);
        IsEmptyState = _settings.Servers.Count == 0;
        RefreshFreshness();

        foreach (var name in new[]
                 {
                     nameof(Sections), nameof(DownSpeedText), nameof(UpSpeedText),
                     nameof(ActiveTorrentsText), nameof(FreeSpaceText),
                     nameof(HeaderServerCount), nameof(IsEmptyState), nameof(SelectedMode),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static Uri SafeWebUri(ServerConfig server)
    {
        try { return server.WebUiUri(); }
        catch { return new Uri("about:blank"); }
    }

    private static string SegmentText(TorrentRowInfoSegment segment) => segment switch
    {
        TorrentRowInfoSegment.Down down => $"{ByteRateFormatter.DownArrow} {ByteRateFormatter.Rate(down.Rate)}",
        TorrentRowInfoSegment.Up up => $"{ByteRateFormatter.UpArrow} {ByteRateFormatter.Rate(up.Rate)}",
        TorrentRowInfoSegment.Eta eta => L10n.Eta(eta.Seconds),
        TorrentRowInfoSegment.StateWord state => L10n.StateWord(state.State),
        _ => "",
    };

    public static IBrush StateBrush(TorrentState state) => state switch
    {
        TorrentState.Downloading => Brushes.DodgerBlue,
        TorrentState.Seeding => Brushes.MediumSeaGreen,
        TorrentState.Stalled => Brushes.Orange,
        TorrentState.Error => Brushes.IndianRed,
        _ => Brushes.Gray,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
