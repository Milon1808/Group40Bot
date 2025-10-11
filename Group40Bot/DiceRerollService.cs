using Discord;
using Discord.WebSocket;
using Group40Bot;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Listens for 🔁 reactions and re-executes the stored expression.
/// **Anyone** can trigger the reroll. The new message again contains 🔁 and is tracked.
/// Includes robust user-resolution and correct MessageDeleted signature.
/// </summary>
public sealed class DiceRerollService(
    DiscordSocketClient client,
    IDiceRoller roller,
    RollMemory memory,
    ILogger<DiceRerollService> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.ReactionAdded += OnReactionAdded;

        // MessageDeleted has (message, channel) signature
        client.MessageDeleted += (msg, ch) =>
        {
            memory.Remove(msg.Id);
            return Task.CompletedTask;
        };

        return Task.CompletedTask;
    }

    private async Task OnReactionAdded(Cacheable<IUserMessage, ulong> msg, Cacheable<IMessageChannel, ulong> ch, SocketReaction r)
    {
        try
        {
            if (r.UserId == client.CurrentUser.Id) return;
            if (r.Emote is not Emoji e || e.Name != "🔁") return;
            if (!memory.TryGet(msg.Id, out var stored)) return;

            var channel = await ch.GetOrDownloadAsync() as ISocketMessageChannel;
            if (channel is null) return;

            var res = roller.Evaluate(stored.Expr);

            // Resolve reacting user to an IUser if possible
            IUser? actor =
                (channel as SocketGuildChannel)?.Guild.GetUser(r.UserId) ??
                client.GetUser(r.UserId);

            Embed embed = actor is not null
                ? DiceEmbeds.BuildResultEmbed(actor, res)
                : BuildFallbackEmbed(r.UserId, res);

            var outMsg = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
            await outMsg.AddReactionAsync(new Emoji("🔁"));
            memory.Store(outMsg.Id, new StoredRoll(res.Canonical));

            log.LogInformation("[Dice] 🔁 by:{User} in:{Guild} expr:'{Expr}'",
                r.UserId, (channel as SocketGuildChannel)?.Guild.Id, stored.Expr);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Dice] 🔁 reroll failed msg:{Msg}", msg.Id);
        }
    }

    /// <summary>
    /// Builds an embed when we cannot resolve an IUser from cache.
    /// Mirrors DiceEmbeds.BuildResultEmbed structure, but uses mention text.
    /// </summary>
    private static Embed BuildFallbackEmbed(ulong userId, RollResult res)
    {
        var eb = new EmbedBuilder()
            .WithTitle($"🎲 Roll by <@{userId}>")
            .WithColor(new Color(0x5865F2))
            .WithFooter($"Expr: {res.Canonical}")
            .WithCurrentTimestamp();

        if (res.Kind == RollKind.Warhammer && res.Warhammer is not null)
        {
            eb.AddField("Target", res.Warhammer.Target.ToString(), inline: true);
            eb.AddField("Rolls", string.Join("\n", res.Warhammer.RollLines), inline: false);
        }
        else
        {
            eb.AddField("Total", res.Total.ToString(), inline: true);
            eb.AddField("Details", string.IsNullOrWhiteSpace(res.Breakdown) ? "-" : res.Breakdown, inline: false);
        }

        return eb.Build();
    }
}