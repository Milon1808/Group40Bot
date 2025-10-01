using System.Text;
using Discord;
using Discord.Interactions;

namespace Group40Bot;

/// <summary>
/// Global /help command. Builds full syntax for all slash commands.
/// Replies ephemeral to avoid channel noise.
/// </summary>
public sealed class CoreModule(InteractionService svc) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("help", "Shows all available commands with syntax")]
    public async Task Help()
    {
        string Pretty(string t) => t switch
        {
            "SocketVoiceChannel" => "voice-channel",
            "SocketTextChannel" => "text-channel",
            "SocketRole" => "role",
            "Boolean" => "bool",
            "String" => "string",
            _ => t
        };

        var lines = svc.SlashCommands
            .OrderBy(c => c.Module.SlashGroupName ?? string.Empty)
            .ThenBy(c => c.Name)
            .Select(c =>
            {
                var path = c.Module.SlashGroupName is null ? $"/{c.Name}" : $"/{c.Module.SlashGroupName} {c.Name}";
                var args = c.Parameters.Count == 0 ? ""
                    : " " + string.Join(" ", c.Parameters.Select(p =>
                        p.IsRequired ? $"{p.Name}:<{Pretty(p.ParameterType.Name)}>"
                                     : $"[{p.Name}:<{Pretty(p.ParameterType.Name)}>]"));
                var desc = string.IsNullOrWhiteSpace(c.Description) ? "" : $" — {c.Description}";
                return $"{path}{args}{desc}";
            });

        var body = new StringBuilder().AppendLine("Commands")
            .AppendLine("```")
            .AppendLine(string.Join("\n", lines))
            .AppendLine("```").ToString();

        var embed = new EmbedBuilder()
            .WithTitle("Help")
            .WithDescription(body)
            .WithColor(new Color(0x5865F2))
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }
}
