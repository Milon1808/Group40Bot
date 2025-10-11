using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Abstraction for guild-scoped settings:
/// - Temp voice lobbies per guild
/// - Reaction-role messages per guild
/// Adds structured logging with UTC timestamps and full context.
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

    // Giveaways
    Task AddOrUpdateGiveawayAsync(GiveawayEntry entry);
    Task<List<GiveawayEntry>> ListGiveawaysAsync(ulong guildId);
    Task<GiveawayEntry?> GetGiveawayByIdAsync(ulong guildId, Guid id);
    Task<GiveawayEntry?> GetGiveawayByMessageAsync(ulong guildId, ulong messageId);
    Task RemoveGiveawayAsync(ulong guildId, Guid id);
}

/// <summary>
/// File-backed implementation designed for servers:
/// - Resolves a stable data directory (ENV -> config -> OS default)
/// - Logs the absolute file path and load results
/// - Backs up corrupt JSON and starts empty
/// - Atomic write (temp + replace)
/// - Backward compatible with legacy lobby-only schema
/// </summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

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
        _log.LogInformation("[{Ts}] Settings file: {Path}", Ts(), _file);

        if (!File.Exists(_file))
        {
            _log.LogInformation("[{Ts}] Settings file not found. Starting with empty store.", Ts());
            return;
        }

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

            _log.LogInformation("[{Ts}] Settings loaded. Guilds(lobbies):{L} Guilds(reactionRoles):{R}",
                Ts(), _root.Lobbies.Count, _root.ReactionRoles.Count);
        }
        catch (JsonException ex)
        {
            var backup = _file + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            try { File.Move(_file, backup, true); } catch { /* ignore move failure */ }
            _log.LogWarning(ex, "[{Ts}] Settings corrupted. Moved to {Backup}. Starting empty.", Ts(), backup);
            _root = new SettingsRoot();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[{Ts}] Failed to load settings; starting empty", Ts());
            _root = new SettingsRoot();
        }
    }

        // -------- Giveaways --------
    public async Task AddOrUpdateGiveawayAsync(GiveawayEntry entry)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_root.Giveaways.TryGetValue(entry.GuildId, out var list))
                    _root.Giveaways[entry.GuildId] = list = new List<GiveawayEntry>();

                var idx = list.FindIndex(g => g.Id == entry.Id);
                if (idx >= 0) list[idx] = entry; else list.Add(entry);
                Save();

                _log.LogInformation("[{Ts}] gw/save guild:{G} id:{Id} status:{S} start:{Start:u} end:{End:u}",
                    DateTimeOffset.UtcNow, entry.GuildId, entry.Id, entry.Status, entry.StartUtc, entry.EndUtc);
            }
            finally { _lock.Release(); }
        }

    public Task<List<GiveawayEntry>> ListGiveawaysAsync(ulong guildId)
    {
        if (_root.Giveaways.TryGetValue(guildId, out var list))
            return Task.FromResult(list.OrderBy(g => g.StartUtc).ToList());
        return Task.FromResult(new List<GiveawayEntry>());
    }

    public Task<GiveawayEntry?> GetGiveawayByIdAsync(ulong guildId, Guid id)
    {
        if (_root.Giveaways.TryGetValue(guildId, out var list))
            return Task.FromResult<GiveawayEntry?>(list.FirstOrDefault(g => g.Id == id));
        return Task.FromResult<GiveawayEntry?>(null);
    }

    public Task<GiveawayEntry?> GetGiveawayByMessageAsync(ulong guildId, ulong messageId)
    {
        if (_root.Giveaways.TryGetValue(guildId, out var list))
            return Task.FromResult<GiveawayEntry?>(list.FirstOrDefault(g => g.MessageId == messageId));
        return Task.FromResult<GiveawayEntry?>(null);
    }

    public async Task RemoveGiveawayAsync(ulong guildId, Guid id)
    {
        await _lock.WaitAsync();
        try
        {
            if (_root.Giveaways.TryGetValue(guildId, out var list))
            {
                var before = list.Count;
                list.RemoveAll(g => g.Id == id);
                if (before != list.Count) Save();
            }
        }
        finally { _lock.Release(); }
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
        try
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

            _log.LogInformation("[{Ts}] Settings saved. Lobbies:{L} ReactionMessages:{R}",
                Ts(), _root.Lobbies.Sum(kv => kv.Value.Count), _root.ReactionRoles.Sum(kv => kv.Value.Count));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[{Ts}] Settings save failed", Ts());
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

            var added = set.Add(channelId);
            Save();

            _log.LogInformation("[{Ts}] lobby/add guild:{Guild} channel:{Chan} added:{Added} total:{Total}",
                Ts(), guildId, channelId, added, set.Count);
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
                var removed = set.Remove(channelId);
                Save();

                _log.LogInformation("[{Ts}] lobby/remove guild:{Guild} channel:{Chan} removed:{Removed} remaining:{Total}",
                    Ts(), guildId, channelId, removed, set.Count);
            }
            else
            {
                _log.LogInformation("[{Ts}] lobby/remove guild:{Guild} no-set", Ts(), guildId);
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
            var isUpdate = idx >= 0;
            if (isUpdate) list[idx] = entry; else list.Add(entry);

            Save();

            _log.LogInformation("[{Ts}] rr/add guild:{Guild} msg:{Msg} pairs:{Pairs} updated:{Updated} totalForGuild:{Total}",
                Ts(), entry.GuildId, entry.MessageId, entry.Pairs.Count, isUpdate, list.Count);
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
                var before = list.Count;
                list.RemoveAll(x => x.MessageId == messageId);
                var removed = before - list.Count;

                Save();

                _log.LogInformation("[{Ts}] rr/remove guild:{Guild} msg:{Msg} removed:{Removed} remaining:{Total}",
                    Ts(), guildId, messageId, removed, list.Count);
            }
            else
            {
                _log.LogInformation("[{Ts}] rr/remove guild:{Guild} no-list", Ts(), guildId);
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
    public Dictionary<ulong, List<GiveawayEntry>> Giveaways { get; set; } = new();
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

public enum GiveawayStatus
{
    Scheduled = 0,
    Active = 1,
    Completed = 2,
    Cancelled = 3
}

/// <summary>One scheduled/active/completed giveaway persisted to disk.</summary>
public sealed class GiveawayEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; } // set when posted
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string EmojiKey { get; set; } = "🎉";
    public string EmojiRaw { get; set; } = "🎉";
    public List<ulong> ExcludedRoleIds { get; set; } = new();
    public bool ExcludeBots { get; set; } = true;

    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public GiveawayStatus Status { get; set; } = GiveawayStatus.Scheduled;

    //winner (null if not decided yet)
    public ulong? WinnerUserId { get; set; }

    //Tracked participants
    public HashSet<ulong> ParticipantIds { get; set; } = new();
}
