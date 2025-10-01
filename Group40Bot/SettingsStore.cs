using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Abstraction for guild-scoped settings:
/// - Temp voice lobbies per guild
/// - Reaction-role messages per guild
/// </summary>
public interface ISettingsStore
{
    // Temp-voice
    Task<HashSet<ulong>> GetLobbiesAsync(ulong guildId);
    Task AddLobbyAsync(ulong guildId, ulong channelId);
    Task RemoveLobbyAsync(ulong guildId, ulong channelId);

    // Reaction-roles
    Task AddReactionRoleAsync(ReactionRoleEntry entry);
    Task RemoveReactionRoleAsync(ulong guildId, ulong messageId);
    Task<ReactionRoleEntry?> GetReactionRoleByMessageAsync(ulong guildId, ulong messageId);
    Task<List<ReactionRoleEntry>> ListReactionRolesAsync(ulong guildId);
}

/// <summary>
/// File-backed implementation designed for servers:
/// - Resolves a stable data directory (ENV -> config -> OS default)
/// - Logs the absolute file path
/// - Backs up corrupt JSON and starts empty
/// - Atomic write (temp + replace)
/// - Backward compatible with legacy lobby-only schema
/// </summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _file;
    private readonly ILogger<FileSettingsStore> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SettingsRoot _root = new();

    public FileSettingsStore(IConfiguration cfg, ILogger<FileSettingsStore> log)
    {
        _log = log;

        var dir = ResolveDataDir(cfg);
        Directory.CreateDirectory(dir);

        _file = Path.Combine(dir, "settings.json");
        _log.LogInformation("Settings file: {Path}", _file);

        if (!File.Exists(_file))
            return;

        try
        {
            var json = File.ReadAllText(_file);
            _root = JsonSerializer.Deserialize<SettingsRoot>(json) ?? new SettingsRoot();

            // Back-compat: legacy was Dictionary<guildId, HashSet<lobbyIds>>
            if (_root.Lobbies.Count == 0 && _root.ReactionRoles.Count == 0)
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<ulong, HashSet<ulong>>>(json);
                if (legacy != null) _root.Lobbies = legacy;
            }
        }
        catch (JsonException ex)
        {
            var backup = _file + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            try { File.Move(_file, backup, true); } catch { }
            _log.LogWarning(ex, "Settings corrupted. Moved to {Backup}. Starting empty.", backup);
            _root = new SettingsRoot();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load settings; starting empty");
            _root = new SettingsRoot();
        }
    }

    /// <summary>Resolve data directory: ENV:DATA_DIR -> config:DataDir -> OS default.</summary>
    private static string ResolveDataDir(IConfiguration cfg)
    {
        var env = Environment.GetEnvironmentVariable("DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var cfgDir = cfg["DataDir"];
        if (!string.IsNullOrWhiteSpace(cfgDir)) return cfgDir;

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Group40Bot");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "Group40Bot");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Group40Bot");
    }

    /// <summary>Atomic write: temp file + replace to avoid torn writes.</summary>
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var json = JsonSerializer.Serialize(_root, new JsonSerializerOptions { WriteIndented = true });

        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, json);
        try { File.Replace(tmp, _file, null); }
        catch
        {
            if (File.Exists(_file)) File.Delete(_file);
            File.Move(tmp, _file);
        }
    }

    // -------- Temp-voice --------
    public Task<HashSet<ulong>> GetLobbiesAsync(ulong guildId) =>
        Task.FromResult(_root.Lobbies.TryGetValue(guildId, out var v) ? new HashSet<ulong>(v) : new());

    public async Task AddLobbyAsync(ulong guildId, ulong channelId)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_root.Lobbies.TryGetValue(guildId, out var set))
                _root.Lobbies[guildId] = set = new HashSet<ulong>();
            set.Add(channelId);
            Save();
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveLobbyAsync(ulong guildId, ulong channelId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_root.Lobbies.TryGetValue(guildId, out var set))
            {
                set.Remove(channelId);
                Save();
            }
        }
        finally { _lock.Release(); }
    }

    // -------- Reaction-roles --------
    public async Task AddReactionRoleAsync(ReactionRoleEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_root.ReactionRoles.TryGetValue(entry.GuildId, out var list))
                _root.ReactionRoles[entry.GuildId] = list = new List<ReactionRoleEntry>();

            var idx = list.FindIndex(x => x.MessageId == entry.MessageId);
            if (idx >= 0) list[idx] = entry; else list.Add(entry);

            Save();
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveReactionRoleAsync(ulong guildId, ulong messageId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_root.ReactionRoles.TryGetValue(guildId, out var list))
            {
                list.RemoveAll(x => x.MessageId == messageId);
                Save();
            }
        }
        finally { _lock.Release(); }
    }

    public Task<ReactionRoleEntry?> GetReactionRoleByMessageAsync(ulong guildId, ulong messageId)
    {
        if (_root.ReactionRoles.TryGetValue(guildId, out var list))
            return Task.FromResult<ReactionRoleEntry?>(list.FirstOrDefault(x => x.MessageId == messageId));
        return Task.FromResult<ReactionRoleEntry?>(null);
    }

    public Task<List<ReactionRoleEntry>> ListReactionRolesAsync(ulong guildId)
    {
        if (_root.ReactionRoles.TryGetValue(guildId, out var list))
            return Task.FromResult(list.ToList());
        return Task.FromResult(new List<ReactionRoleEntry>());
    }
}

/// <summary>Root JSON document persisted to disk.</summary>
public sealed class SettingsRoot
{
    public Dictionary<ulong, HashSet<ulong>> Lobbies { get; set; } = new();
    public Dictionary<ulong, List<ReactionRoleEntry>> ReactionRoles { get; set; } = new();
}

/// <summary>One reaction-role message and its emoji-to-role mapping.</summary>
public sealed class ReactionRoleEntry
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public bool RemoveOnUnreact { get; set; }
    public List<ReactionRolePair> Pairs { get; set; } = new();
}

/// <summary>Mapping from a single emoji key to a role id.</summary>
public sealed class ReactionRolePair
{
    public string EmojiKey { get; set; } = "";
    public ulong RoleId { get; set; }
    public string? EmojiRaw { get; set; }
    public ReactionRolePair() { }
    public ReactionRolePair(string emojiKey, ulong roleId, string? emojiRaw)
        => (EmojiKey, RoleId, EmojiRaw) = (emojiKey, roleId, emojiRaw);
}
