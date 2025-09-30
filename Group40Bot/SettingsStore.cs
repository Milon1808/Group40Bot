using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/*
SUMMARY (EN):
- File-backed store with two sections:
  - Lobbies: per-guild set of lobby voice-channel IDs.
  - ReactionRoles: per-guild list of reaction-role entries.
- Backward compatible: if old format is found, it is wrapped into the new root schema.
*/

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

public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _file;
    private readonly ILogger<FileSettingsStore> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SettingsRoot _root = new();

    public FileSettingsStore(IConfiguration cfg, ILogger<FileSettingsStore> log)
    {
        _log = log;
        var dir = Environment.GetEnvironmentVariable("DATA_DIR") ?? cfg["DataDir"] ?? "./data";
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "settings.json");

        if (File.Exists(_file))
        {
            try
            {
                var json = File.ReadAllText(_file);
                // Try new format first
                _root = JsonSerializer.Deserialize<SettingsRoot>(json) ?? new SettingsRoot();

                // Back-compat: old file was Dictionary<guildId, HashSet<lobbyIds>>
                if (_root.Lobbies.Count == 0 && _root.ReactionRoles.Count == 0)
                {
                    var old = JsonSerializer.Deserialize<Dictionary<ulong, HashSet<ulong>>>(json);
                    if (old != null) _root.Lobbies = old;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load settings; starting empty.");
                _root = new SettingsRoot();
            }
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_file, json);
    }

    // ----- Temp-voice -----
    public Task<HashSet<ulong>> GetLobbiesAsync(ulong guildId)
        => Task.FromResult(_root.Lobbies.TryGetValue(guildId, out var v) ? new HashSet<ulong>(v) : new());

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

    // ----- Reaction-roles -----
    public async Task AddReactionRoleAsync(ReactionRoleEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_root.ReactionRoles.TryGetValue(entry.GuildId, out var list))
                _root.ReactionRoles[entry.GuildId] = list = new List<ReactionRoleEntry>();

            // Replace if same message
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

// ----- Data models -----
public sealed class SettingsRoot
{
    public Dictionary<ulong, HashSet<ulong>> Lobbies { get; set; } = new();
    public Dictionary<ulong, List<ReactionRoleEntry>> ReactionRoles { get; set; } = new();
}

public sealed class ReactionRoleEntry
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public bool RemoveOnUnreact { get; set; }
    public List<ReactionRolePair> Pairs { get; set; } = new();
}

public sealed class ReactionRolePair
{
    public string EmojiKey { get; set; } = "";
    public ulong RoleId { get; set; }
    public string? EmojiRaw { get; set; }
    public ReactionRolePair() { }
    public ReactionRolePair(string emojiKey, ulong roleId, string? emojiRaw)
        => (EmojiKey, RoleId, EmojiRaw) = (emojiKey, roleId, emojiRaw);
}
