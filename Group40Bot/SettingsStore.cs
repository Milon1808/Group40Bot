using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/*
SUMMARY (EN):
- Simple file-based store: per-guild set of lobby voice-channel IDs.
- Thread-safe via SemaphoreSlim. Data persisted to DATA_DIR/settings.json.
- This keeps configurations cleanly separated across guilds.
*/

public interface ISettingsStore
{
    Task<HashSet<ulong>> GetLobbiesAsync(ulong guildId);
    Task AddLobbyAsync(ulong guildId, ulong channelId);
    Task RemoveLobbyAsync(ulong guildId, ulong channelId);
}

public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _file;
    private readonly ILogger<FileSettingsStore> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ConcurrentDictionary<ulong, HashSet<ulong>> _map = new();

    public FileSettingsStore(IConfiguration cfg, ILogger<FileSettingsStore> log)
    {
        _log = log;
        var root = Environment.GetEnvironmentVariable("DATA_DIR") ?? cfg["DataDir"] ?? "./data";
        Directory.CreateDirectory(root);
        _file = Path.Combine(root, "settings.json");

        if (File.Exists(_file))
        {
            try
            {
                var json = File.ReadAllText(_file);
                var data = JsonSerializer.Deserialize<Dictionary<ulong, HashSet<ulong>>>(json);
                if (data != null) _map = new(data);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to load settings; starting empty"); }
        }
    }

    public Task<HashSet<ulong>> GetLobbiesAsync(ulong guildId) =>
        Task.FromResult(_map.TryGetValue(guildId, out var v) ? new HashSet<ulong>(v) : new());

    public async Task AddLobbyAsync(ulong guildId, ulong channelId)
    {
        await _lock.WaitAsync();
        try { _map.GetOrAdd(guildId, _ => new()).Add(channelId); Save(); }
        finally { _lock.Release(); }
    }

    public async Task RemoveLobbyAsync(ulong guildId, ulong channelId)
    {
        await _lock.WaitAsync();
        try { if (_map.TryGetValue(guildId, out var s)) { s.Remove(channelId); Save(); } }
        finally { _lock.Release(); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_file, json);
    }
}
