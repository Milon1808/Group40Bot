using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/*
SUMMARY (EN):
- Listens to UserVoiceStateUpdated.
- If a user joins a configured "lobby" voice channel, create a personal temp channel
  in the same category (inherits permissions), grant owner+bot extra privileges,
  move the user into it, and delete the channel when empty.
- Settings are per guild via ISettingsStore.
*/

public sealed class TempVoiceService(DiscordSocketClient client, ISettingsStore store, ILogger<TempVoiceService> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, ulong> _userTemp = new(); // userId -> tempChannelId

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdated;
        return Task.CompletedTask;
    }

    private async Task OnVoiceStateUpdated(SocketUser u, SocketVoiceState before, SocketVoiceState after)
    {
        if (u is not SocketGuildUser user) return;
        var guild = user.Guild;

        // Join lobby?
        if (after.VoiceChannel is { } lobby)
        {
            var lobbies = await store.GetLobbiesAsync(guild.Id);
            if (lobbies.Contains(lobby.Id))
            {
                // Reuse existing personal channel
                if (_userTemp.TryGetValue(user.Id, out var existingId) &&
                    guild.GetVoiceChannel(existingId) is { } existing)
                {
                    try { await user.ModifyAsync(p => p.Channel = existing); } catch { /* recreate below */ }
                    return;
                }

                var category = lobby.Category;

                // Create channel without overwrites so it inherits category permissions (visible like its category)
                var chan = await guild.CreateVoiceChannelAsync(user.DisplayName, props =>
                {
                    if (category != null) props.CategoryId = category.Id;
                });

                // Extra rights for the bot and the owner
                var me = guild.CurrentUser;
                if (me != null)
                {
                    await chan.AddPermissionOverwriteAsync(me, new OverwritePermissions(
                        viewChannel: PermValue.Allow, connect: PermValue.Allow,
                        manageChannel: PermValue.Allow, manageRoles: PermValue.Allow, moveMembers: PermValue.Allow));
                }
                await chan.AddPermissionOverwriteAsync(user, new OverwritePermissions(
                    viewChannel: PermValue.Allow, connect: PermValue.Allow,
                    manageChannel: PermValue.Allow, manageRoles: PermValue.Allow));

                _userTemp[user.Id] = chan.Id;

                try { await user.ModifyAsync(p => p.Channel = chan); } catch { /* ignore */ }
            }
        }

        // Left own temp channel -> delete if empty
        if (before.VoiceChannel is { } prev &&
            _userTemp.TryGetValue(user.Id, out var tempId) &&
            tempId == prev.Id &&
            prev.ConnectedUsers.Count == 0)
        {
            try { await prev.DeleteAsync(new RequestOptions { AuditLogReason = "Temp VC empty" }); }
            finally { _userTemp.TryRemove(user.Id, out _); }
        }
    }
}
