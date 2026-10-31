using PTMonitor.Core.Model;

namespace PTMonitor.App.Services;

/// <summary>
/// String tables for English and Simplified Chinese. Keys are the English
/// strings themselves; the lookup falls back to the key when a language or a
/// key is missing. Language applies immediately (no restart needed).
/// </summary>
public static class L10n
{
    public static AppLanguage Language { get; private set; } = AppLanguage.System;

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["just now"] = "刚刚",
        ["%llds ago"] = "%lld 秒前",
        ["%lldm ago"] = "%lld 分钟前",
        ["%lldh ago"] = "%lld 小时前",
        ["%lld server · %lld online"] = "%lld 台服务器 · %lld 台在线",
        ["%lld servers · %lld online"] = "%lld 台服务器 · %lld 台在线",
        ["Not configured"] = "未配置",
        ["%lld · %lld total"] = "%lld · 共 %lld",
        ["Downloading"] = "下载中",
        ["Seeding"] = "做种中",
        ["Stalled"] = "停滞",
        ["Queued"] = "排队中",
        ["Checking"] = "校验中",
        ["Paused"] = "已暂停",
        ["Error"] = "错误",
        ["Unknown"] = "未知",
        ["%lldh %02lldm"] = "%lld小时 %02lld分",
        ["%lldm %02llds"] = "%lld分 %02lld秒",
        ["%llds"] = "%lld秒",
        ["No torrents."] = "没有种子。",
        ["No downloading torrents."] = "没有正在下载的种子。",
        ["No uploading torrents."] = "没有正在上传的种子。",
        ["No active torrents."] = "没有活动中的种子。",
        ["Connecting…"] = "连接中…",
        ["Disabled"] = "已禁用",
        ["%lld more torrents"] = "还有 %lld 个种子",
        ["Open %@ web UI"] = "打开 %@ 的网页界面",
        ["Unreachable"] = "无法连接",
        ["Sign-in failed"] = "登录失败",
        ["Temporarily banned"] = "被暂时封禁",
        ["Unexpected response"] = "异常响应",
        ["Server error"] = "服务器错误",
        ["Connected: %@"] = "已连接：%@",
        ["Failed: %@"] = "失败：%@",
        ["enter a host name or IP address"] = "请输入主机名或 IP 地址",
        ["port must be a whole number between 1 and 65535"] = "端口必须是 1 到 65535 之间的整数",
        ["Used port %lld from the host field."] = "已使用主机字段中的端口 %lld。",
        ["Enabled HTTPS because the host starts with https://."] = "主机以 https:// 开头，已启用 HTTPS。",
        ["Disabled HTTPS because the host starts with http://."] = "主机以 http:// 开头，已禁用 HTTPS。",
        ["PT Monitor Preferences"] = "PT Monitor 偏好设置",
        ["Servers"] = "服务器",
        ["No servers configured. Add a qBittorrent or Transmission server to start monitoring."] =
            "尚未配置服务器。添加一个 qBittorrent 或 Transmission 服务器以开始监控。",
        ["Add Server…"] = "添加服务器…",
        ["Add Server"] = "添加服务器",
        ["Edit Server"] = "编辑服务器",
        ["Edit"] = "编辑",
        ["Delete"] = "删除",
        ["Save"] = "保存",
        ["Cancel"] = "取消",
        ["Test Connection"] = "测试连接",
        ["Testing…"] = "测试中…",
        ["Type"] = "类型",
        ["Name"] = "名称",
        ["Host"] = "主机",
        ["Port"] = "端口",
        ["Use HTTPS"] = "使用 HTTPS",
        ["Allow self-signed certificate"] = "允许自签名证书",
        ["User name"] = "用户名",
        ["Password"] = "密码",
        ["Enabled"] = "启用",
        ["Enable or disable this server"] = "启用或禁用此服务器",
        ["Poll every %lld seconds"] = "每 %lld 秒轮询一次",
        ["Show speeds in the tray tooltip"] = "在托盘提示中显示速度",
        ["Show floating speed window"] = "显示悬浮速度窗口",
        ["Open PT Monitor"] = "打开 PT Monitor",
        ["PT Monitor speeds"] = "PT Monitor 速度",
        ["Torrents shown in the panel"] = "面板中显示的种子",
        ["Language"] = "语言",
        ["Follow System"] = "跟随系统",
        ["Refresh Now"] = "立即刷新",
        ["Preferences…"] = "偏好设置…",
        ["Quit PT Monitor"] = "退出 PT Monitor",
        ["↓ Speed"] = "↓ 速度",
        ["↑ Speed"] = "↑ 速度",
        ["Active Torrents"] = "活动种子",
        ["Free Space"] = "剩余空间",
        ["All"] = "全部",
        ["↓ Down"] = "↓ 下载",
        ["↑ Up"] = "↑ 上传",
        ["Active"] = "活动",
        ["No servers configured"] = "未配置服务器",
        ["Size"] = "大小",
        ["Ratio"] = "分享率",
        ["Seeds / Leechers"] = "种子 / 下载者",
        ["Tracker"] = "Tracker",
        ["Category"] = "分类",
        ["Working"] = "正常",
        ["Not working"] = "异常",
        ["Could not save the password (%@). Nothing was saved."] = "无法保存密码（%@）。未保存任何更改。",
    };

    public static void SetLanguage(AppLanguage language)
    {
        Language = language;
        if (language != AppLanguage.System) return;
        // Resolve "system" to the OS language once, for this session.
        var name = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            Language = AppLanguage.Chinese;
        else
            Language = AppLanguage.English;
    }

    public static string S(string key) =>
        Language == AppLanguage.Chinese && Zh.TryGetValue(key, out var value) ? value : key;

    public static string Format(string key, params object[] arguments)
    {
        var template = S(key);
        // The tables keep printf-style %lld/%@ placeholders; translate to .NET {0}…{n}.
        var result = new System.Text.StringBuilder();
        var argumentIndex = 0;
        for (var index = 0; index < template.Length; index++)
        {
            var rest = template.AsSpan(index);
            if (rest.StartsWith("%lld"))
            {
                result.Append(arguments[argumentIndex++]);
                index += 3;
            }
            else if (rest.StartsWith("%@"))
            {
                result.Append(arguments[argumentIndex++]);
                index += 1;
            }
            else
            {
                result.Append(template[index]);
            }
        }
        return result.ToString();
    }

    // MARK: - Formatted helpers

    public static string Freshness(DateTimeOffset? fetched, DateTimeOffset now)
    {
        if (fetched is null) return "—";
        var seconds = (int)Math.Max(0, (now - fetched.Value).TotalSeconds);
        if (seconds < 5) return S("just now");
        if (seconds < 60) return Format("%llds ago", seconds);
        var minutes = seconds / 60;
        if (minutes < 60) return Format("%lldm ago", minutes);
        return Format("%lldh ago", minutes / 60);
    }

    public static string ServerCount(int total, int online) =>
        total <= 0
            ? S("Not configured")
            : Format(total == 1 ? "%lld server · %lld online" : "%lld servers · %lld online", total, online);

    public static string ActiveTorrents(int active, int total) => Format("%lld · %lld total", active, total);

    public static string StateWord(TorrentState state) => state switch
    {
        TorrentState.Downloading => S("Downloading"),
        TorrentState.Seeding => S("Seeding"),
        TorrentState.Stalled => S("Stalled"),
        TorrentState.Queued => S("Queued"),
        TorrentState.Checking => S("Checking"),
        TorrentState.Paused => S("Paused"),
        TorrentState.Error => S("Error"),
        _ => S("Unknown"),
    };

    public static string Eta(long? seconds)
    {
        if (seconds is null or < 0) return "—";
        var value = seconds.Value;
        var hours = value / 3600;
        var minutes = value % 3600 / 60;
        if (hours > 0) return Format("%lldh %02lldm", hours, minutes);
        if (minutes > 0) return Format("%lldm %02llds", minutes, value % 60);
        return Format("%llds", value);
    }

    public static string ListMode(TorrentListMode mode) => mode switch
    {
        TorrentListMode.All => S("All"),
        TorrentListMode.Downloading => S("↓ Down"),
        TorrentListMode.Uploading => S("↑ Up"),
        _ => S("Active"),
    };

    public static string NoTorrents(TorrentListMode mode) => mode switch
    {
        TorrentListMode.All => S("No torrents."),
        TorrentListMode.Downloading => S("No downloading torrents."),
        TorrentListMode.Uploading => S("No uploading torrents."),
        _ => S("No active torrents."),
    };

    public static string CauseHeadline(ServerFailure failure) => failure.Kind switch
    {
        FailureKind.Unreachable => S("Unreachable"),
        FailureKind.Auth => S("Sign-in failed"),
        FailureKind.Banned => S("Temporarily banned"),
        FailureKind.ProtocolViolation => S("Unexpected response"),
        _ => S("Server error"),
    };

    public static string OpenInWebUi(string serverName) => Format("Open %@ web UI", serverName);
    public static string Connected(string version) => Format("Connected: %@", version);
    public static string Failed(string detail) => Format("Failed: %@", detail);
    public static string MoreTorrents(int count) => Format("%lld more torrents", count);
}
