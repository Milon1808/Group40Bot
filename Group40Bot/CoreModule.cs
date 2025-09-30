using System.Text;
using Discord.Interactions;

namespace Group40Bot;

/*
SUMMARY (EN):
- /help lists all slash commands with argument syntax.
- Types are rendered in angle brackets; optional args in [brackets].
*/

public sealed class CoreModule(InteractionService svc) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Shows all available commands with syntax")]
    public async Task Help()
    {
        string Pretty(string n) => n switch
        {
            "SocketVoiceChannel" => "voice-channel",
            "SocketTextChannel" => "text-channel",
            "SocketRole" => "role",
            "Boolean" => "bool",
            "String" => "string",
            _ => n
        };

        var lines = svc.SlashCommands
            .OrderBy(c => c.Module.SlashGroupName ?? string.Empty)
            .ThenBy(c => c.Name)
            .Select(c =>
            {
                var path = c.Module.SlashGroupName is null ? $"/{c.Name}" : $"/{c.Module.SlashGroupName} {c.Name}";
                var args = c.Parameters.Count == 0
                    ? ""
                    : " " + string.Join(" ", c.Parameters.Select(p =>
                        p.IsRequired
                            ? $"{p.Name}:<{Pretty(p.ParameterType.Name)}>"
                            : $"[{p.Name}:<{Pretty(p.ParameterType.Name)}>]"));
                var desc = string.IsNullOrWhiteSpace(c.Description) ? "" : $" — {c.Description}";
                return $"{path}{args}{desc}";
            });

        var msg = new StringBuilder()
            .AppendLine("**Commands**")
            .AppendLine(string.Join("\n", lines))
            .ToString();

        await RespondAsync(msg, ephemeral: false);
    }
}
