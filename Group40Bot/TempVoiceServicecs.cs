using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Creates per-user temporary voice channels when a user joins a configured lobby.
/// Inherits category permissions, optionally grants extra rights to bot and owner
/// (only if the bot may manage the channel), moves the user, and deletes the temp
/// channel when empty. Includes per-user gating and robust logging.
/// </summary>
public sealed class TempVoiceService(DiscordSocketClient client, ISettingsStore store, ILogger<TempVoiceService> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, ulong> _userTemp = new(); // userId -> tempChannelId
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _userGates = new(); // per-user gate

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdatedSafe;
        return Task.CompletedTask;
    }

    private async Task OnVoiceStateUpdatedSafe(SocketUser u, SocketVoiceState before, SocketVoiceState after)
    {
        if (u is not SocketGuildUser user) return;

        var gate = _userGates.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await HandleVoiceUpdate(user, before, after); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "TempVoice error guild:{Guild} user:{User} before:{B} after:{A}",
                user.Guild.Id, user.Id, before.VoiceChannel?.Id, after.VoiceChannel?.Id);
        }
        finally { try { gate.Release(); } catch { } }
    }

    private async Task HandleVoiceUpdate(SocketGuildUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var guild = user.Guild;

        if (after.VoiceChannel is { } joined)
        {
            var lobbies = await store.GetLobbiesAsync(guild.Id);
            if (lobbies.Contains(joined.Id))
            {
                if (_userTemp.TryGetValue(user.Id, out var existingId) &&
                    guild.GetVoiceChannel(existingId) is { } existingChan)
                {
                    try { await user.ModifyAsync(p => p.Channel = existingChan); } catch { }
                    return;
                }

                var category = joined.Category;

                try
                {
                    var created = await guild.CreateVoiceChannelAsync(user.DisplayName, props =>
                    {
                        if (category != null) props.CategoryId = category.Id; // inherit category perms
                    });

                    var me = guild.CurrentUser;
                    if (me != null && me.GetPermissions((IGuildChannel)created).ManageChannel)
                    {
                        try
                        {
                            await created.AddPermissionOverwriteAsync(me, new OverwritePermissions(
                                viewChannel: PermValue.Allow, connect: PermValue.Allow,
                                manageChannel: PermValue.Allow, manageRoles: PermValue.Allow, moveMembers: PermValue.Allow));

                            await created.AddPermissionOverwriteAsync(user, new OverwritePermissions(
                                viewChannel: PermValue.Allow, connect: PermValue.Allow,
                                manageChannel: PermValue.Allow, manageRoles: PermValue.Allow));
                        }
                        catch (Exception ex)
                        {
                            log.LogDebug(ex, "Overwrite set failed channel:{Chan}", created.Id);
                        }
                    }
                    else
                    {
                        log.LogInformation("Skip overwrites: bot lacks ManageChannel guild:{Guild} channel:{Chan}",
                            guild.Id, created.Id);
                    }

                    _userTemp[user.Id] = created.Id;

                    try
                    {
                        if (me != null && me.GetPermissions((IGuildChannel)created).MoveMembers)
                            await user.ModifyAsync(p => p.Channel = created);
                    }
                    catch (Exception ex) { log.LogDebug(ex, "Move to created temp failed"); }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Create voice channel failed guild:{Guild} lobby:{Lobby}", guild.Id, joined.Id);
                }

                return;
            }
        }

        if (before.VoiceChannel is { } left &&
            _userTemp.TryGetValue(user.Id, out var tempId) &&
            tempId == left.Id &&
            left.ConnectedUsers.Count == 0)
        {
            try { await left.DeleteAsync(new RequestOptions { AuditLogReason = "Temp VC empty" }); }
            catch (Exception ex) { log.LogDebug(ex, "Delete temp channel failed channel:{Chan}", left.Id); }
            finally { _userTemp.TryRemove(user.Id, out _); }
        }
    }
}
