using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Group40Bot;

/// <summary>
/// Admin-only commands to configure temporary voice channels per guild.
/// Non-list commands reply ephemeral to reduce channel noise.
/// </summary>
[Group("tempvoice", "Configure temporary voice channels")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class InteractionModule(ISettingsStore store) : InteractionModuleBase<SocketInteractionContext>
{
    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    [SlashCommand("add-lobby", "Mark a voice channel as lobby")]
    public async Task AddLobby([Summary("lobby")] SocketVoiceChannel lobby)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        await store.AddLobbyAsync(Context.Guild.Id, lobby.Id);
        await RespondAsync($":white_check_mark: Lobby set: {MentionUtils.MentionChannel(lobby.Id)}", ephemeral: true);
    }

    [SlashCommand("remove-lobby", "Unmark a lobby")]
    public async Task RemoveLobby([Summary("lobby")] SocketVoiceChannel lobby)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        await store.RemoveLobbyAsync(Context.Guild.Id, lobby.Id);
        await RespondAsync($":white_check_mark: Removed: {MentionUtils.MentionChannel(lobby.Id)}", ephemeral: true);
    }

    [SlashCommand("list", "List all lobbies")]
    public async Task List()
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        var ids = await store.GetLobbiesAsync(Context.Guild.Id);
        var text = ids.Count == 0 ? "_none_" : string.Join("\n", ids.Select(MentionUtils.MentionChannel));
        await RespondAsync(text, ephemeral: false); // list remains public
    }
}
