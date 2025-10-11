using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Slash command /r to roll dice with a compact syntax.
/// Supports:
/// - Arithmetic dice expressions with operators: e.g. "3d6+2", "1d8!!-1", "2d6*3-4"
///   * Explosion with "!!" means re-rolling and adding while the max face is rolled.
/// - Warhammer test syntax: "<N>d100w<T>[+|-M]". Examples: "d100w50", "3d100w50", "2d100w45+10".
///   * SL = floor(T/10) - floor(R/10).
///   * Doubles under target ⇒ critical success; doubles over target ⇒ critical failure.
///   * **Auto-crit (d100): 1–5 ⇒ critical success, 96–100 ⇒ critical failure.**
/// - Adds 🔁 reaction; **anyone** reacting will re-execute the same expression.
/// </summary>
public sealed class DiceModule(IDiceRoller roller, RollMemory memory, ILogger<DiceModule> log)
  : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("r", "Roll dice (e.g. 3d6+2, 1d8!!-1, 3d100w50)")]
    public async Task RollAsync([Summary(description: "Dice expression")] string expr)
    {
        log.LogInformation("[Dice] /r by:{User} in:{Guild} expr:'{Expr}'",
            Context.User.Id, Context.Guild?.Id, expr);

        RollResult result;
        try
        {
            result = roller.Evaluate(expr);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[Dice] parse/eval failed expr:'{Expr}'", expr);
            await RespondAsync($":warning: Invalid dice expression.\n`{ex.Message}`", ephemeral: true);
            return;
        }

        var embed = DiceEmbeds.BuildResultEmbed(Context.User, result);
        var outMsg = await Context.Channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);

        // Add reroll reaction (for everyone) and remember the expression for this message
        await outMsg.AddReactionAsync(new Emoji("🔁"));
        memory.Store(outMsg.Id, new StoredRoll(result.Canonical));

        await RespondAsync(":white_check_mark: Roll posted.", ephemeral: true);
    }
}

/// <summary>
/// Small embed helper to keep Module tidy.
/// </summary>
internal static class DiceEmbeds
{
    public static Embed BuildResultEmbed(IUser user, RollResult res)
    {
        var eb = new EmbedBuilder()
            .WithTitle($"🎲 Roll by {user.Username}")
            .WithColor(new Color(0x5865F2))
            .WithFooter($"Expr: {res.Canonical}")
            .WithCurrentTimestamp();

        if (res.Kind == RollKind.Warhammer)
        {
            eb.AddField("Target", res.Warhammer!.Target.ToString(), inline: true);
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