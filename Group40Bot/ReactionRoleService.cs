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
/// </summary>
public sealed class ReactionRoleService(DiscordSocketClient client, ISettingsStore store, ILogger<ReactionRoleService> log)
    : BackgroundService
{
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
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel; if (channel is null) return;
        var entry = await store.GetReactionRoleByMessageAsync(channel.Guild.Id, msg.Id); if (entry is null) return;

        var key = KeyFrom(r.Emote);
        var pair = entry.Pairs.FirstOrDefault(p => p.EmojiKey == key); if (pair is null) return;

        var role = channel.Guild.GetRole(pair.RoleId); if (role is null) return;
        var user = channel.Guild.GetUser(r.UserId); if (user is null) return;

        try { if (!user.Roles.Any(x => x.Id == role.Id)) await user.AddRoleAsync(role); }
        catch (Exception ex) { log.LogWarning(ex, "AddRole failed user:{User} role:{Role}", r.UserId, role.Id); }
    }

    private async Task OnRemoved(Cacheable<IUserMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch, SocketReaction r)
    {
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel; if (channel is null) return;
        var entry = await store.GetReactionRoleByMessageAsync(channel.Guild.Id, msg.Id); if (entry is null || !entry.RemoveOnUnreact) return;

        var key = KeyFrom(r.Emote);
        var pair = entry.Pairs.FirstOrDefault(p => p.EmojiKey == key); if (pair is null) return;

        var role = channel.Guild.GetRole(pair.RoleId); if (role is null) return;
        var user = channel.Guild.GetUser(r.UserId); if (user is null) return;

        try { if (user.Roles.Any(x => x.Id == role.Id)) await user.RemoveRoleAsync(role); }
        catch (Exception ex) { log.LogWarning(ex, "RemoveRole failed user:{User} role:{Role}", r.UserId, role.Id); }
    }

    private async Task OnDeleted(Cacheable<IMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch)
    {
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel; if (channel is null) return;
        await store.RemoveReactionRoleAsync(channel.Guild.Id, msg.Id);
    }
}
