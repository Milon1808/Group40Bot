using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Global /help command. Builds full syntax for all slash commands.
/// Adds structured logging with UTC timestamps and guild/user context.
/// Replies ephemeral to avoid channel noise.
/// </summary>
public sealed class CoreModule(InteractionService svc, ILogger<CoreModule> log) : InteractionModuleBase<SocketInteractionContext>
{
    // --- Logging helpers (UTC timestamp + context) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private (string ts, string gname, ulong gid, string uname, ulong uid) CtxBase()
        => (Ts(), Context.Guild?.Name ?? "<no-guild>", Context.Guild?.Id ?? 0UL,
            Context.User?.Username ?? "<user>", Context.User?.Id ?? 0UL);

    [SlashCommand("help", "Shows all available commands with syntax")]
    public async Task Help()
    {
        var (ts0, gname0, gid0, uname0, uid0) = CtxBase();
        log.LogInformation("[{Ts}] help begin guild:{GName}({GId}) by:{User}({Uid})",
            ts0, gname0, gid0, uname0, uid0);

        try
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

            var cmds = svc.SlashCommands
                .OrderBy(c => c.Module.SlashGroupName ?? string.Empty)
                .ThenBy(c => c.Name)
                .ToList();

            log.LogInformation("[{Ts}] help discovered {Count} commands guild:{GName}({GId})",
                Ts(), cmds.Count, gname0, gid0);

            if (cmds.Count == 0)
            {
                await RespondAsync(":information_source: No commands registered.", ephemeral: true);
                log.LogInformation("[{Ts}] help no-commands guild:{GName}({GId})", Ts(), gname0, gid0);
                return;
            }

            var lines = cmds.Select(c =>
            {
                var path = c.Module.SlashGroupName is null ? $"/{c.Name}" : $"/{c.Module.SlashGroupName} {c.Name}";
                var args = c.Parameters.Count == 0 ? ""
                    : " " + string.Join(" ", c.Parameters.Select(p =>
                        p.IsRequired ? $"{p.Name}:<{Pretty(p.ParameterType.Name)}>"
                                     : $"[{p.Name}:<{Pretty(p.ParameterType.Name)}>]"));
                var desc = string.IsNullOrWhiteSpace(c.Description) ? "" : $" — {c.Description}";
                return $"{path}{args}{desc}";
            });

            var body = new StringBuilder()
                .AppendLine("Commands")
                .AppendLine("```")
                .AppendLine(string.Join("\n", lines))
                .AppendLine("```")
                .ToString();

            var embed = new EmbedBuilder()
                .WithTitle("Help")
                .WithDescription(body)
                .WithColor(new Color(0x5865F2))
                .Build();

            await RespondAsync(embed: embed, ephemeral: true);

            log.LogInformation("[{Ts}] help ok guild:{GName}({GId}) by:{User}({Uid}) count:{Count}",
                Ts(), gname0, gid0, uname0, uid0, cmds.Count);
        }
        catch (Discord.Net.HttpException ex)
        {
            var (tsE, gnameE, gidE, _, _) = CtxBase();
            log.LogError(ex, "[{Ts}] help HttpException http={Http} code={Code} reason={Reason} guild:{GName}({GId})",
                tsE, ex.HttpCode, ex.DiscordCode, ex.Reason, gnameE, gidE);

            await RespondAsync($":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**\n" +
                               $"• Guild: **{gnameE}** (`{gidE}`)\n" +
                               $"• Time: `{tsE}`",
                               ephemeral: true);
        }
        catch (Exception ex)
        {
            var (tsU, gnameU, gidU, _, _) = CtxBase();
            log.LogError(ex, "[{Ts}] help failed guild:{GName}({GId})", tsU, gnameU, gidU);

            await RespondAsync(":warning: Failed to build help. See logs for details.\n" +
                               $"• Guild: **{gnameU}** (`{gidU}`)\n" +
                               $"• Time: `{tsU}`",
                               ephemeral: true);
        }
    }
}
