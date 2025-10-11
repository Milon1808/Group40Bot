using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Group40Bot;

/// <summary>
/// Runs the giveaway lifecycle:
/// - Posts scheduled giveaways at StartUtc with embed + reaction
/// - Tracks participants via reactions during Active window
/// - Picks winner at EndUtc and announces
/// - Supports multiple concurrent giveaways per guild
/// </summary>
public sealed class GiveawayService(
    DiscordSocketClient client,
    ISettingsStore store,
    ILogger<GiveawayService> log) : BackgroundService
{
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private static string EmoteKey(IEmote e) => e is Emote ce ? ce.Id.ToString() : e.Name;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.ReactionAdded += OnReactionAdded;
        client.ReactionRemoved += OnReactionRemoved;
        _ = LoopAsync(stoppingToken);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[{Ts}] gw/tick failed", Ts());
            }

            try { await timer.WaitForNextTickAsync(ct); } catch { break; }
        }
    }

    private async Task TickAsync()
    {
        foreach (var g in client.Guilds)
        {
            var list = await store.ListGiveawaysAsync(g.Id);
            var now = DateTimeOffset.UtcNow;

            foreach (var gw in list)
            {
                if (gw.Status == GiveawayStatus.Scheduled && now >= gw.StartUtc)
                    await StartGiveawayAsync(g, gw);

                if (gw.Status == GiveawayStatus.Active && now >= gw.EndUtc)
                    await CompleteGiveawayAsync(g, gw);
            }
        }
    }

    private async Task StartGiveawayAsync(SocketGuild guild, GiveawayEntry gw)
    {
        try
        {
            var channel = guild.GetTextChannel(gw.ChannelId);
            if (channel is null) { log.LogWarning("[{Ts}] gw/start no-channel guild:{G} id:{Id}", Ts(), guild.Id, gw.Id); return; }

            var embed = new EmbedBuilder()
                .WithTitle($"🎁 {gw.Title}")
                .WithDescription($"{gw.Body}\n\nReact with {gw.EmojiRaw} to participate.\nStarts: <t:{gw.StartUtc.ToUnixTimeSeconds()}:F>\nEnds: <t:{gw.EndUtc.ToUnixTimeSeconds()}:F>")
                .WithColor(new Color(0x57F287))
                .WithFooter($"Giveaway ID: {gw.Id}")
                .Build();

            var msg = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
            gw.MessageId = msg.Id;
            gw.Status = GiveawayStatus.Active;

            // Add the participation reaction
            if (Emote.TryParse(gw.EmojiRaw, out var custom))
                await msg.AddReactionAsync(custom);
            else
                await msg.AddReactionAsync(new Emoji(gw.EmojiRaw));

            await store.AddOrUpdateGiveawayAsync(gw);

            log.LogInformation("[{Ts}] gw/started guild:{G} id:{Id} msg:{Msg}", Ts(), guild.Id, gw.Id, gw.MessageId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Ts}] gw/start failed guild:{G} id:{Id}", Ts(), guild.Id, gw.Id);
        }
    }

    private async Task CompleteGiveawayAsync(SocketGuild guild, GiveawayEntry gw)
    {
        try
        {
            var channel = guild.GetTextChannel(gw.ChannelId);
            if (channel is null) { log.LogWarning("[{Ts}] gw/complete no-channel guild:{G} id:{Id}", Ts(), guild.Id, gw.Id); return; }

            var eligible = FilterEligible(guild, gw);
            gw.Status = GiveawayStatus.Completed;

            if (eligible.Count == 0)
            {
                gw.WinnerUserId = null;
                await store.AddOrUpdateGiveawayAsync(gw);

                await channel.SendMessageAsync(embed: new EmbedBuilder()
                    .WithTitle($"🎁 {gw.Title}")
                    .WithDescription("No eligible participants.")
                    .WithColor(new Color(0xED4245))
                    .Build());
                log.LogInformation("[{Ts}] gw/complete no-eligible guild:{G} id:{Id}", Ts(), guild.Id, gw.Id);
                return;
            }

            var winnerId = PickRandom(eligible);
            gw.WinnerUserId = winnerId;
            await store.AddOrUpdateGiveawayAsync(gw);

            var winner = guild.GetUser(winnerId);
            var eb = new EmbedBuilder()
                .WithTitle($"🎉 Winner: {(winner != null ? winner.Username : $"<@{winnerId}>")}")
                .WithDescription($"{gw.Body}\n\nCongratulations <@{winnerId}>!")
                .WithColor(new Color(0xFEE75C))
                .WithFooter($"Giveaway ID: {gw.Id}")
                .Build();

            var allowed = new AllowedMentions { AllowedTypes = AllowedMentionTypes.None };
            allowed.UserIds.Add(winnerId);

            await channel.SendMessageAsync(text: $"<@{winnerId}>", embed: eb, allowedMentions: allowed);
            log.LogInformation("[{Ts}] gw/complete winner guild:{G} id:{Id} winner:{W}", Ts(), guild.Id, gw.Id, winnerId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[{Ts}] gw/complete failed guild:{G} id:{Id}", Ts(), guild.Id, gw.Id);
        }
    }


    private List<ulong> FilterEligible(SocketGuild guild, GiveawayEntry gw)
    {
        var set = new HashSet<ulong>(gw.ParticipantIds);
        foreach (var uid in gw.ParticipantIds.ToArray())
        {
            var u = guild.GetUser(uid);
            if (u is null) { set.Remove(uid); continue; }
            if (gw.ExcludeBots && (u.IsBot || u.IsWebhook)) { set.Remove(uid); continue; }
            if (u.Roles.Any(r => gw.ExcludedRoleIds.Contains(r.Id))) { set.Remove(uid); continue; }
        }
        return set.ToList();
    }

    private static ulong PickRandom(List<ulong> items)
    {
        var span = CollectionsMarshal.AsSpan(items);
        var idx = RandomNumberGenerator.GetInt32(span.Length);
        return span[idx];
    }

    // Reaction handlers: accept during Active window only and matching emoji
    private async Task OnReactionAdded(Cacheable<IUserMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch, SocketReaction r)
    {
        if (r.UserId == client.CurrentUser.Id) return;
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel; if (channel is null) return;

        var gw = await store.GetGiveawayByMessageAsync(channel.Guild.Id, msg.Id);
        if (gw is null || gw.Status != GiveawayStatus.Active) return;

        var key = EmoteKey(r.Emote);
        if (key != gw.EmojiKey) return;

        gw.ParticipantIds.Add(r.UserId);
        await store.AddOrUpdateGiveawayAsync(gw);

        log.LogInformation("[{Ts}] gw/react+ guild:{G} id:{Id} user:{U} total:{N}", Ts(), channel.Guild.Id, gw.Id, r.UserId, gw.ParticipantIds.Count);
    }

    private async Task OnReactionRemoved(Cacheable<IUserMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch, SocketReaction r)
    {
        var channel = await ch.GetOrDownloadAsync() as SocketTextChannel; if (channel is null) return;

        var gw = await store.GetGiveawayByMessageAsync(channel.Guild.Id, msg.Id);
        if (gw is null || gw.Status != GiveawayStatus.Active) return;

        var key = EmoteKey(r.Emote);
        if (key != gw.EmojiKey) return;

        gw.ParticipantIds.Remove(r.UserId);
        await store.AddOrUpdateGiveawayAsync(gw);

        log.LogInformation("[{Ts}] gw/react- guild:{G} id:{Id} user:{U} total:{N}", Ts(), channel.Guild.Id, gw.Id, r.UserId, gw.ParticipantIds.Count);
    }
}
