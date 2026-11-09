using System;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Atlas;

/// <summary>Where the Atlas read token is kept. Never inside the plugin configuration XML, never returned to the config page.</summary>
public interface ITokenStore
{
    bool Has { get; }

    string Read();

    void Write(string token);

    void Clear();
}

/// <summary>
/// A file in the server's plugin configuration folder (admin-readable only: on Unix the mode is set to 0600, on Windows the folder's
/// own ACL applies). Not encrypted: Jellyfin has no per-user key store for plugins (unverified), so the protection is the file system,
/// as CONTRACT-C15 8.4 says for Jellyfin.
/// </summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string _path;
    private readonly object _gate = new object();

    public FileTokenStore(string path)
    {
        _path = path;
    }

    public bool Has => !string.IsNullOrEmpty(Read());

    public string Read()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_path) ? File.ReadAllText(_path, Encoding.UTF8).Trim() : string.Empty;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    public void Write(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Clear();
            return;
        }

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, token.Trim(), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, _path, true);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // a token that cannot be deleted is reported by Has staying true
            }
        }
    }
}

/// <summary>An in-memory store for tests.</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private string _token = string.Empty;

    public bool Has => !string.IsNullOrEmpty(_token);

    public string Read() => _token;

    public void Write(string token) => _token = (token ?? string.Empty).Trim();

    public void Clear() => _token = string.Empty;
}
