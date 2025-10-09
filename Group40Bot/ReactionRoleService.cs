using System;
using System.Linq;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Runtime handler for reaction-role messages:
/// - Adds role on reaction add
/// - Optionally removes role on reaction remove
/// - Cleans mapping when the message gets deleted
/// Adds structured logging with UTC timestamps and full guild/channel/user context.
/// </summary>
public sealed class ReactionRoleService(DiscordSocketClient client, ISettingsStore store, ILogger<ReactionRoleService> log)
    : BackgroundService
{
    // --- Logging helper (UTC timestamp) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.ReactionAdded += OnAdded;
        client.ReactionRemoved += OnRemoved;
        client.MessageDeleted += OnDeleted;
        return Task.CompletedTask;
    }

    private static string KeyFrom(IEmote e) => e is Emote ce ? ce.Id.ToString() : e.Name;

    private async Task OnAdded(Cacheable<IUserMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch, SocketReaction r)
    {
        if (r.UserId == client.CurrentUser.Id) return;

        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel;
        if (channel is null) return;

        var guild = channel.Guild;
        var key = KeyFrom(r.Emote);

        log.LogInformation("[{Ts}] react/add begin guild:{GName}({GId}) chan:{CName}({CId}) msg:{Msg} user:{User} emote:{EmoteKey}",
            Ts(), guild.Name, guild.Id, channel.Name, channel.Id, msg.Id, r.UserId, key);

        var entry = await store.GetReactionRoleByMessageAsync(guild.Id, msg.Id);
        if (entry is null)
        {
            log.LogInformation("[{Ts}] react/add no-entry guild:{GId} msg:{Msg}", Ts(), guild.Id, msg.Id);
            return;
        }

        var pair = entry.Pairs.FirstOrDefault(p => p.EmojiKey == key);
        if (pair is null)
        {
            log.LogInformation("[{Ts}] react/add no-pair guild:{GId} msg:{Msg} emote:{EmoteKey}", Ts(), guild.Id, msg.Id, key);
            return;
        }

        var role = guild.GetRole(pair.RoleId);
        if (role is null)
        {
            log.LogInformation("[{Ts}] react/add role-missing guild:{GId} role:{Role}", Ts(), guild.Id, pair.RoleId);
            return;
        }

        var user = guild.GetUser(r.UserId);
        if (user is null)
        {
            log.LogInformation("[{Ts}] react/add user-missing guild:{GId} user:{User}", Ts(), guild.Id, r.UserId);
            return;
        }

        try
        {
            if (user.Roles.Any(x => x.Id == role.Id))
            {
                log.LogInformation("[{Ts}] react/add already-has-role guild:{GId} user:{User} role:{Role}", Ts(), guild.Id, user.Id, role.Id);
                return;
            }

            await user.AddRoleAsync(role);
            log.LogInformation("[{Ts}] react/add ok guild:{GId} user:{User} role:{Role}", Ts(), guild.Id, user.Id, role.Id);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Ts}] react/add failed guild:{GId} user:{User} role:{Role}", Ts(), guild.Id, r.UserId, role.Id);
        }
    }

    private async Task OnRemoved(Cacheable<IUserMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch, SocketReaction r)
    {
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel;
        if (channel is null) return;

        var guild = channel.Guild;
        var key = KeyFrom(r.Emote);

        log.LogInformation("[{Ts}] react/remove begin guild:{GName}({GId}) chan:{CName}({CId}) msg:{Msg} user:{User} emote:{EmoteKey}",
            Ts(), guild.Name, guild.Id, channel.Name, channel.Id, msg.Id, r.UserId, key);

        var entry = await store.GetReactionRoleByMessageAsync(guild.Id, msg.Id);
        if (entry is null || !entry.RemoveOnUnreact)
        {
            log.LogInformation("[{Ts}] react/remove skip guild:{GId} msg:{Msg} removeOnUnreact:{Flag}",
                Ts(), guild.Id, msg.Id, entry?.RemoveOnUnreact ?? false);
            return;
        }

        var pair = entry.Pairs.FirstOrDefault(p => p.EmojiKey == key);
        if (pair is null)
        {
            log.LogInformation("[{Ts}] react/remove no-pair guild:{GId} msg:{Msg} emote:{EmoteKey}", Ts(), guild.Id, msg.Id, key);
            return;
        }

        var role = guild.GetRole(pair.RoleId);
        if (role is null)
        {
            log.LogInformation("[{Ts}] react/remove role-missing guild:{GId} role:{Role}", Ts(), guild.Id, pair.RoleId);
            return;
        }

        var user = guild.GetUser(r.UserId);
        if (user is null)
        {
            log.LogInformation("[{Ts}] react/remove user-missing guild:{GId} user:{User}", Ts(), guild.Id, r.UserId);
            return;
        }

        try
        {
            if (!user.Roles.Any(x => x.Id == role.Id))
            {
                log.LogInformation("[{Ts}] react/remove user-has-no-role guild:{GId} user:{User} role:{Role}", Ts(), guild.Id, user.Id, role.Id);
                return;
            }

            await user.RemoveRoleAsync(role);
            log.LogInformation("[{Ts}] react/remove ok guild:{GId} user:{User} role:{Role}", Ts(), guild.Id, user.Id, role.Id);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Ts}] react/remove failed guild:{GId} user:{User} role:{Role}", Ts(), guild.Id, r.UserId, role.Id);
        }
    }

    private async Task OnDeleted(Cacheable<IMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch)
    {
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel;
        if (channel is null) return;

        var guild = channel.Guild;
        log.LogInformation("[{Ts}] react/cleanup begin guild:{GName}({GId}) chan:{CName}({CId}) msg:{Msg}",
            Ts(), guild.Name, guild.Id, channel.Name, channel.Id, msg.Id);

        await store.RemoveReactionRoleAsync(guild.Id, msg.Id);

        log.LogInformation("[{Ts}] react/cleanup ok guild:{GId} msg:{Msg}", Ts(), guild.Id, msg.Id);
    }
}
