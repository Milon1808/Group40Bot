using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Admin-only commands to configure temporary voice channels per guild.
/// Adds structured logging with UTC timestamps and guild/user context.
/// Non-list commands reply ephemeral to reduce channel noise.
/// </summary>
[Group("tempvoice", "Configure temporary voice channels")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class InteractionModule(ISettingsStore store, ILogger<InteractionModule> log)
    : InteractionModuleBase<SocketInteractionContext>
{
    // --- Logging helpers (UTC timestamp + context) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private (string ts, string gname, ulong gid, string uname, ulong uid) CtxBase()
        => (Ts(), Context.Guild?.Name ?? "<no-guild>", Context.Guild?.Id ?? 0UL,
            Context.User?.Username ?? "<user>", Context.User?.Id ?? 0UL);

    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    [SlashCommand("add-lobby", "Mark a voice channel as lobby")]
    public async Task AddLobby([Summary("lobby")] SocketVoiceChannel lobby)
    {
        var (ts, gname, gid, uname, uid) = CtxBase();
        log.LogInformation("[{Ts}] tempvoice/add-lobby begin guild:{GName}({GId}) lobby:{Lobby}({LobbyId}) by:{User}({Uid})",
            ts, gname, gid, lobby?.Name ?? "<null>", lobby?.Id ?? 0UL, uname, uid);

        try
        {
            if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
            if (lobby == null) { await RespondAsync(":warning: Lobby channel not found.", ephemeral: true); return; }

            await store.AddLobbyAsync(Context.Guild.Id, lobby.Id);
            await RespondAsync($":white_check_mark: Lobby set: {MentionUtils.MentionChannel(lobby.Id)}", ephemeral: true);

            log.LogInformation("[{Ts}] tempvoice/add-lobby ok guild:{GName}({GId}) lobby:{Lobby}({LobbyId}) by:{User}({Uid})",
                Ts(), gname, gid, lobby.Name, lobby.Id, uname, uid);
        }
        catch (Discord.Net.HttpException ex)
        {
            log.LogError(ex, "[{Ts}] tempvoice/add-lobby HttpException http={Http} code={Code} reason={Reason} guild:{GName}({GId})",
                Ts(), ex.HttpCode, ex.DiscordCode, ex.Reason, gname, gid);
            await RespondAsync($":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**\n• Guild: **{gname}** (`{gid}`)\n• Time: `{Ts()}`", ephemeral: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] tempvoice/add-lobby failed guild:{GName}({GId})", Ts(), gname, gid);
            await RespondAsync(":warning: Failed to add lobby. See logs for details.\n" +
                               $"• Guild: **{gname}** (`{gid}`)\n• Time: `{Ts()}`", ephemeral: true);
        }
    }

    [SlashCommand("remove-lobby", "Unmark a lobby")]
    public async Task RemoveLobby([Summary("lobby")] SocketVoiceChannel lobby)
    {
        var (ts, gname, gid, uname, uid) = CtxBase();
        log.LogInformation("[{Ts}] tempvoice/remove-lobby begin guild:{GName}({GId}) lobby:{Lobby}({LobbyId}) by:{User}({Uid})",
            ts, gname, gid, lobby?.Name ?? "<null>", lobby?.Id ?? 0UL, uname, uid);

        try
        {
            if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
            if (lobby == null) { await RespondAsync(":warning: Lobby channel not found.", ephemeral: true); return; }

            await store.RemoveLobbyAsync(Context.Guild.Id, lobby.Id);
            await RespondAsync($":white_check_mark: Removed: {MentionUtils.MentionChannel(lobby.Id)}", ephemeral: true);

            log.LogInformation("[{Ts}] tempvoice/remove-lobby ok guild:{GName}({GId}) lobby:{Lobby}({LobbyId}) by:{User}({Uid})",
                Ts(), gname, gid, lobby.Name, lobby.Id, uname, uid);
        }
        catch (Discord.Net.HttpException ex)
        {
            log.LogError(ex, "[{Ts}] tempvoice/remove-lobby HttpException http={Http} code={Code} reason={Reason} guild:{GName}({GId})",
                Ts(), ex.HttpCode, ex.DiscordCode, ex.Reason, gname, gid);
            await RespondAsync($":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**\n• Guild: **{gname}** (`{gid}`)\n• Time: `{Ts()}`", ephemeral: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] tempvoice/remove-lobby failed guild:{GName}({GId})", Ts(), gname, gid);
            await RespondAsync(":warning: Failed to remove lobby. See logs for details.\n" +
                               $"• Guild: **{gname}** (`{gid}`)\n• Time: `{Ts()}`", ephemeral: true);
        }
    }

    [SlashCommand("list", "List all lobbies")]
    public async Task List()
    {
        var (ts, gname, gid, uname, uid) = CtxBase();
        log.LogInformation("[{Ts}] tempvoice/list begin guild:{GName}({GId}) by:{User}({Uid})",
            ts, gname, gid, uname, uid);

        try
        {
            if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

            var ids = await store.GetLobbiesAsync(Context.Guild.Id);
            var text = ids.Count == 0 ? "_none_" : string.Join("\n", ids.Select(MentionUtils.MentionChannel));

            await RespondAsync(text, ephemeral: false); // list remains public

            log.LogInformation("[{Ts}] tempvoice/list ok guild:{GName}({GId}) count:{Count}",
                Ts(), gname, gid, ids.Count);
        }
        catch (Discord.Net.HttpException ex)
        {
            log.LogError(ex, "[{Ts}] tempvoice/list HttpException http={Http} code={Code} reason={Reason} guild:{GName}({GId})",
                Ts(), ex.HttpCode, ex.DiscordCode, ex.Reason, gname, gid);
            await RespondAsync($":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**\n• Guild: **{gname}** (`{gid}`)\n• Time: `{Ts()}`", ephemeral: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] tempvoice/list failed guild:{GName}({GId})", Ts(), gname, gid);
            await RespondAsync(":warning: Failed to list lobbies. See logs for details.\n" +
                               $"• Guild: **{gname}** (`{gid}`)\n• Time: `{Ts()}`", ephemeral: true);
        }
    }
}
