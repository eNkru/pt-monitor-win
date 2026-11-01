using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PTMonitor.Core.Store;

/// <summary>Whether the secret file is present and readable.</summary>
public enum SecretStoreState
{
    /// <summary>The file was read, or the last write succeeded.</summary>
    Ok,

    /// <summary>No file yet — a fresh install.</summary>
    Absent,

    /// <summary>A file exists but could not be read; every secret is unavailable.</summary>
    Unreadable,
}

public interface ISecretStore
{
    /// <summary>State of the backing file; <see cref="SecretStoreState.Ok"/> for in-memory stores.</summary>
    SecretStoreState State { get; }

    string? Get(Guid serverId);

    /// <summary>
    /// Stores (or, for an empty password, removes) one secret.
    /// Throws when the file cannot be written — the caller must abort its own
    /// write, because a server with no stored password would poll anonymously.
    /// The return value is not a success flag: <c>false</c> means "empty password,
    /// entry removed".
    /// </summary>
    bool Set(string password, Guid serverId);

    /// <summary>Removes one secret. Never throws: a failed delete must not break the UI row removal.</summary>
    void Delete(Guid serverId);

    /// <summary>Removes every secret. Never throws, for the same reason as <see cref="Delete"/>.</summary>
    void DeleteAll();
}

/// <summary>
/// Windows DPAPI-backed password storage in <c>secrets.dat</c>.
///
/// Each password is protected with <see cref="DataProtectionScope.CurrentUser"/>,
/// so another Windows account cannot read it. On non-Windows (used for
/// development and tests) the value falls back to base64 with a marker.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const string PlainPrefix = "plain:";
    private readonly object _gate = new();
    private readonly string _path;
    private SecretStoreState _state = SecretStoreState.Absent;
    private Dictionary<string, string> _items = [];

    public DpapiSecretStore(string path)
    {
        _path = path;
        Load();
    }

    public SecretStoreState State
    {
        get { lock (_gate) return _state; }
    }

    public string? Get(Guid serverId)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(serverId.ToString("N"), out var stored)) return null;
            try
            {
                return Decode(stored);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool Set(string password, Guid serverId)
    {
        lock (_gate)
        {
            var key = serverId.ToString("N");
            var hadPrevious = _items.TryGetValue(key, out var previous);
            if (password.Length == 0) _items.Remove(key);
            else _items[key] = Encode(password);

            try
            {
                Persist();
            }
            catch
            {
                // The disk write failed: restore the in-memory view so it keeps
                // matching the file, then let the caller abort its own save.
                if (hadPrevious) _items[key] = previous!;
                else _items.Remove(key);
                throw;
            }

            _state = SecretStoreState.Ok;
            return password.Length > 0;
        }
    }

    public void Delete(Guid serverId)
    {
        lock (_gate)
        {
            _items.Remove(serverId.ToString("N"));
            PersistOrIgnore();
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            _items.Clear();
            PersistOrIgnore();
        }
    }

    private static string Encode(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        if (!OperatingSystem.IsWindows()) return PlainPrefix + Convert.ToBase64String(bytes);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Decode(string stored)
    {
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
        var bytes = Convert.FromBase64String(stored);
        var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _state = SecretStoreState.Absent;
                return;
            }
            _items = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
            _state = SecretStoreState.Ok;
        }
        catch (Exception)
        {
            _items = [];
            _state = SecretStoreState.Unreadable;
        }
    }

    /// <summary>Writes the current items atomically. Throws when the write fails.</summary>
    private void Persist()
    {
        PreserveUnreadableCopy();
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items));
        File.Move(temp, _path, true);
    }

    /// <summary>
    /// Before overwriting a file that could not be read, keep its bytes as
    /// <c>secrets.dat.corrupt</c>. This runs for every persist (a <c>Delete</c>
    /// rewrites the file too), so the only recovery copy is never destroyed.
    /// </summary>
    private void PreserveUnreadableCopy()
    {
        if (_state != SecretStoreState.Unreadable) return;

        // State stays Unreadable until a Set succeeds, so a later persist would
        // otherwise move the freshly written (valid) file over the original
        // damaged bytes. The first copy wins; it is the one worth recovering.
        // Checked explicitly rather than by relying on File.Move throwing when
        // the destination exists: that is an incidental guarantee, not the rule.
        if (File.Exists(_path + ".corrupt")) return;

        try
        {
            File.Move(_path, _path + ".corrupt");
        }
        catch (Exception)
        {
            // Best effort: a preserved copy is not worth failing the user's write.
        }
    }

    /// <summary>Best-effort write for the delete paths, which must never raise.</summary>
    private void PersistOrIgnore()
    {
        try
        {
            Persist();
        }
        catch (Exception)
        {
            // A failed delete leaves the entry on disk; the row is already gone
            // from the UI and there is no second error path to reach the user.
        }
    }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<Guid, string> _items = [];

    public SecretStoreState State => SecretStoreState.Ok;

    public string? Get(Guid serverId) => _items.GetValueOrDefault(serverId);

    public bool Set(string password, Guid serverId)
    {
        if (password.Length == 0)
        {
            _items.Remove(serverId);
            return false;
        }
        _items[serverId] = password;
        return true;
    }

    public void Delete(Guid serverId) => _items.Remove(serverId);

    public void DeleteAll() => _items.Clear();
}

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDirectory, "PTMonitor");
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
    public static string LogFile => Path.Combine(DataDirectory, "ptmonitor.log");
}
