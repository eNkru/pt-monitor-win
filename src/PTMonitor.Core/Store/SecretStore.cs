using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PTMonitor.Core.Store;

public interface ISecretStore
{
    string? Get(Guid serverId);
    bool Set(string password, Guid serverId);
    void Delete(Guid serverId);
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
    private Dictionary<string, string> _items = [];

    public DpapiSecretStore(string path)
    {
        _path = path;
        Load();
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
            if (password.Length == 0)
            {
                _items.Remove(key);
                PersistUnsafe();
                return false;
            }
            _items[key] = Encode(password);
            PersistUnsafe();
            return true;
        }
    }

    public void Delete(Guid serverId)
    {
        lock (_gate)
        {
            _items.Remove(serverId.ToString("N"));
            PersistUnsafe();
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            _items.Clear();
            PersistUnsafe();
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
            if (!File.Exists(_path)) return;
            _items = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception)
        {
            _items = [];
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items));
            File.Move(temp, _path, true);
        }
        catch (Exception)
        {
            // Secrets persistence failure is surfaced by the preferences layer only
            // where it matters (saving a server).
        }
    }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<Guid, string> _items = [];

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
