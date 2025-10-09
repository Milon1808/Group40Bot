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
/// channel when empty. Includes per-user gating and robust logging with UTC timestamps.
/// </summary>
public sealed class TempVoiceService(DiscordSocketClient client, ISettingsStore store, ILogger<TempVoiceService> log)
    : BackgroundService
{
    // userId -> tempChannelId
    private readonly ConcurrentDictionary<ulong, ulong> _userTemp = new();
    // per-user gate to avoid double creation on rapid events
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _userGates = new();

    // --- Logging helper (UTC timestamp) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdatedSafe;
        return Task.CompletedTask;
    }

    private async Task OnVoiceStateUpdatedSafe(SocketUser u, SocketVoiceState before, SocketVoiceState after)
    {
        if (u is not SocketGuildUser user) return;

        log.LogInformation("[{Ts}] tv/update guild:{GId} user:{Uid} before:{B} after:{A}",
            Ts(), user.Guild.Id, user.Id, before.VoiceChannel?.Id, after.VoiceChannel?.Id);

        var gate = _userGates.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await HandleVoiceUpdate(user, before, after);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Ts}] tv/error guild:{GId} user:{Uid} before:{B} after:{A}",
                Ts(), user.Guild.Id, user.Id, before.VoiceChannel?.Id, after.VoiceChannel?.Id);
        }
        finally { try { gate.Release(); } catch { } }
    }

    private async Task HandleVoiceUpdate(SocketGuildUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var guild = user.Guild;

        // Joined a channel?
        if (after.VoiceChannel is { } joined)
        {
            var lobbies = await store.GetLobbiesAsync(guild.Id);
            if (lobbies.Contains(joined.Id))
            {
                log.LogInformation("[{Ts}] tv/join-lobby guild:{GId} user:{Uid} lobby:{Lobby}", Ts(), guild.Id, user.Id, joined.Id);

                // Reuse existing temp channel if present
                if (_userTemp.TryGetValue(user.Id, out var existingId) &&
                    guild.GetVoiceChannel(existingId) is { } existingChan)
                {
                    try
                    {
                        await user.ModifyAsync(p => p.Channel = existingChan);
                        log.LogInformation("[{Ts}] tv/reuse guild:{GId} user:{Uid} chan:{Cid}", Ts(), guild.Id, user.Id, existingChan.Id);
                    }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "[{Ts}] tv/reuse-move-failed guild:{GId} user:{Uid} chan:{Cid}", Ts(), guild.Id, user.Id, existingChan.Id);
                    }
                    return;
                }

                var category = joined.Category;

                try
                {
                    // Create temp channel in the same category to inherit permissions
                    var created = await guild.CreateVoiceChannelAsync(user.DisplayName, props =>
                    {
                        if (category != null) props.CategoryId = category.Id;
                    });
                    log.LogInformation("[{Ts}] tv/created guild:{GId} user:{Uid} chan:{Cid} cat:{Cat}",
                        Ts(), guild.Id, user.Id, created.Id, category?.Id);

                    var me = guild.CurrentUser;

                    // Only set explicit overwrites if bot has ManageChannel on the created channel
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

                            log.LogInformation("[{Ts}] tv/overwrites-ok guild:{GId} chan:{Cid}", Ts(), guild.Id, created.Id);
                        }
                        catch (Exception ex)
                        {
                            // Continue with inherited perms
                            log.LogDebug(ex, "[{Ts}] tv/overwrites-failed guild:{GId} chan:{Cid}", Ts(), guild.Id, created.Id);
                        }
                    }
                    else
                    {
                        log.LogInformation("[{Ts}] tv/overwrites-skip guild:{GId} chan:{Cid} reason:no-manage-permission",
                            Ts(), guild.Id, created.Id);
                    }

                    _userTemp[user.Id] = created.Id;

                    // Move the user into their temp channel if allowed
                    try
                    {
                        if (me != null && me.GetPermissions((IGuildChannel)created).MoveMembers)
                        {
                            await user.ModifyAsync(p => p.Channel = created);
                            log.LogInformation("[{Ts}] tv/moved guild:{GId} user:{Uid} -> chan:{Cid}", Ts(), guild.Id, user.Id, created.Id);
                        }
                        else
                        {
                            log.LogInformation("[{Ts}] tv/move-skip guild:{GId} user:{Uid} chan:{Cid} reason:no-move-permission",
                                Ts(), guild.Id, user.Id, created.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "[{Ts}] tv/move-failed guild:{GId} user:{Uid} chan:{Cid}", Ts(), guild.Id, user.Id, created.Id);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "[{Ts}] tv/create-failed guild:{GId} lobby:{Lobby}", Ts(), guild.Id, joined.Id);
                }

                return;
            }
        }

        // Left own temp channel -> delete if empty
        if (before.VoiceChannel is { } left &&
            _userTemp.TryGetValue(user.Id, out var tempId) &&
            tempId == left.Id &&
            left.ConnectedUsers.Count == 0)
        {
            try
            {
                await left.DeleteAsync(new RequestOptions { AuditLogReason = "Temp VC empty" });
                log.LogInformation("[{Ts}] tv/deleted guild:{GId} chan:{Cid}", Ts(), guild.Id, left.Id);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "[{Ts}] tv/delete-failed guild:{GId} chan:{Cid}", Ts(), guild.Id, left.Id);
            }
            finally
            {
                _userTemp.TryRemove(user.Id, out _);
            }
        }
    }
}
