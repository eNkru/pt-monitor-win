using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PTMonitor.Core.Diagnostics;

/// <summary>
/// The one file sink: <c>%APPDATA%\PTMonitor\ptmonitor.log</c> plus at most one
/// rotated predecessor (<c>ptmonitor.log.1</c>), so the pair is capped at
/// 2 × <see cref="DefaultMaxBytes"/>.
///
/// Events only — startup, unhandled exceptions, poll-failure <em>transitions</em>,
/// secret-store trouble, TLS certificate acceptance, settings write failures.
/// Never per-poll chatter: at the default 2 s interval that would be the whole
/// file.
///
/// Every call is a no-op until <see cref="Initialize"/> runs, so the CLI, the
/// selftest and the xUnit suite stay free of file side effects. No write ever
/// throws: a full disk or an unwritable <c>%APPDATA%</c> must not break polling.
/// </summary>
public static class Log
{
    /// <summary>Cap of one file; rotation keeps at most two.</summary>
    public const long DefaultMaxBytes = 512 * 1024;

    /// <summary>Suffix of the single rotated predecessor.</summary>
    public const string RotatedSuffix = ".1";

    private static readonly object Gate = new();

    private static string? _path;
    private static long _maxBytes = DefaultMaxBytes;

    /// <summary>
    /// Points the sink at <paramref name="path"/>. Best effort: a path that
    /// cannot be created leaves the sink silent, never the app broken.
    /// </summary>
    public static void Initialize(string path, long maxBytes = DefaultMaxBytes)
    {
        lock (Gate)
        {
            _path = path;
            _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // Cosmetic: the append path retries the same call and swallows its
            // own failure. A missing data directory is not the app's problem.
        }
    }

    /// <summary>Stops writing; every later call is a no-op until the next <see cref="Initialize"/>.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _path = null;
        }
    }

    public static void Info(string area, string message) => Write("info", area, message);

    public static void Warn(string area, string message) => Write("warn", area, message);

    public static void Error(string area, string message) => Write("error", area, message);

    /// <summary>
    /// Masks the secret shapes that must never reach the file. Applied to every
    /// line at write time so a careless call site cannot leak; the primary rule
    /// is still "do not pass secrets to <see cref="Log"/>".
    /// Pure and idempotent — the xUnit and selftest tables pin it.
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // An auth header first: its credential would otherwise be mangled by the
        // userinfo rules below (a base64 token may contain ":" or "@").
        text = AuthorizationHeader.Replace(text, "Authorization: $1 ***");
        // Whole cookie headers, then a bare SID= pair anywhere else.
        text = CookieHeader.Replace(text, "$1: ***");
        text = SidPair.Replace(text, "SID=***");
        // Form and query pairs: password=hunter2, &pass=..., ?pwd=...
        text = CredentialPair.Replace(text, "password=***");
        // URL userinfo, with and without a scheme.
        text = SchemeUserInfo.Replace(text, "$1***@");
        return BareUserInfo.Replace(text, "$1:***@");
    }

    private static readonly Regex AuthorizationHeader = new(
        @"\bAuthorization\s*:\s*([A-Za-z]+)\s+[^\s,;""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CookieHeader = new(
        @"\b(Set-Cookie|Cookie)\s*:\s*[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SidPair = new(
        @"\bSID=[^&\s;,""'}]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CredentialPair = new(
        @"\b(?:password|passwd|pwd|pass)=[^&\s;,""'}]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SchemeUserInfo = new(
        @"\b([A-Za-z][A-Za-z0-9+.\-]*://)[^/\s:@]+:[^/\s@]*@",
        RegexOptions.Compiled);

    private static readonly Regex BareUserInfo = new(
        @"(?<![/\w])([^\s:/@]+):([^\s@/]+)@",
        RegexOptions.Compiled);

    private static void Write(string level, string area, string message)
    {
        lock (Gate)
        {
            if (_path is not { } path) return;
            try
            {
                Append(path, _maxBytes, level, area, message);
            }
            catch (Exception)
            {
                // Cosmetic: an unwritable directory or a full disk must never
                // break polling, so a lost log line stays lost.
            }
        }
    }

    private static void Append(string path, long maxBytes, string level, string area, string message)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(path) && new FileInfo(path).Length >= maxBytes) Rotate(path);

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        // Redact the whole body, not just the message: a careless area label is
        // the same leak. Newlines are flattened so one event is always one line.
        var line = $"{stamp} {OneLine(Redact($"[{level}] {area}: {message}"))}{Environment.NewLine}";

        // Open-append-flush-close per line: volume is events only, and the file
        // survives a hard kill because nothing is buffered past the call.
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>Moves the current file to <c>.1</c>, replacing the previous predecessor.</summary>
    private static void Rotate(string path) => File.Move(path, path + RotatedSuffix, true);

    /// <summary>Joins multi-line text (stack traces) into one line with <c> | </c>.</summary>
    private static string OneLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(" | ", lines);
    }
}
