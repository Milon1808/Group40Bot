using System.Text;
using Discord.Interactions;

namespace Group40Bot;

/*
SUMMARY (EN):
- Global /help command.
- Builds a list of all registered slash commands (including grouped ones) and posts it publicly in the channel.
*/

public sealed class CoreModule(InteractionService svc) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Shows all available commands")]
    public async Task Help()
    {
        var lines = svc.SlashCommands
            .OrderBy(c => c.Module.SlashGroupName ?? string.Empty)
            .ThenBy(c => c.Name)
            .Select(c =>
            {
                var path = c.Module.SlashGroupName is null ? $"/{c.Name}" : $"/{c.Module.SlashGroupName} {c.Name}";
                var desc = string.IsNullOrWhiteSpace(c.Description) ? "" : $" — {c.Description}";
                return $"{path}{desc}";
            });

        var msg = new StringBuilder()
            .AppendLine("**Commands**")
            .AppendLine(string.Join("\n", lines))
            .ToString();

        await RespondAsync(msg, ephemeral: false); // post in channel
    }
}
