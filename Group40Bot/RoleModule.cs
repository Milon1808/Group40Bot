using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Group40Bot;

/*
SUMMARY (EN):
- Admin-only /role register: posts a bilingual role selection message and wires reactions to roles.
- Supports up to 10 (role, emoji) pairs. Emojis can be Unicode (e.g., ✅) or custom (<:name:id>).
- Stores mapping per guild; ReactionRoleService applies roles on react/unreact.
*/

[Group("role", "Self-assign roles via reactions")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class RoleModule(ISettingsStore store) : InteractionModuleBase<SocketInteractionContext>
{
    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    [SlashCommand("register", "Create a reaction-roles message in a channel")]
    public async Task Register(
        [Summary("channel")] SocketTextChannel channel,
        [Summary("role1")] SocketRole role1,
        [Summary("emoji1")] string emoji1,
        [Summary("role2")] SocketRole? role2 = null, [Summary("emoji2")] string? emoji2 = null,
        [Summary("role3")] SocketRole? role3 = null, [Summary("emoji3")] string? emoji3 = null,
        [Summary("role4")] SocketRole? role4 = null, [Summary("emoji4")] string? emoji4 = null,
        [Summary("role5")] SocketRole? role5 = null, [Summary("emoji5")] string? emoji5 = null,
        [Summary("role6")] SocketRole? role6 = null, [Summary("emoji6")] string? emoji6 = null,
        [Summary("role7")] SocketRole? role7 = null, [Summary("emoji7")] string? emoji7 = null,
        [Summary("role8")] SocketRole? role8 = null, [Summary("emoji8")] string? emoji8 = null,
        [Summary("role9")] SocketRole? role9 = null, [Summary("emoji9")] string? emoji9 = null,
        [Summary("role10")] SocketRole? role10 = null, [Summary("emoji10")] string? emoji10 = null,
        [Summary("remove_on_unreact")] bool removeOnUnreact = true)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

        var me = Context.Guild.CurrentUser;
        if (!me.GuildPermissions.ManageRoles)
        { await RespondAsync("Bot is missing **Manage Roles**.", ephemeral: true); return; }

        var pairs = new List<(SocketRole Role, IEmote Emote, string Key, string Raw)>();
        bool TryAdd(SocketRole? role, string? emoji)
        {
            if (role == null || string.IsNullOrWhiteSpace(emoji)) return false;
            if (role.Position >= me.Hierarchy)
                throw new InvalidOperationException($"Bot role must be higher than @{role.Name}.");

            if (Emote.TryParse(emoji, out var custom))
            {
                pairs.Add((role, custom, custom.Id.ToString(), custom.ToString()));
                return true;
            }
            else
            {
                try
                {
                    var e = new Emoji(emoji);
                    pairs.Add((role, e, e.Name, e.Name));
                    return true;
                }
                catch
                {
                    throw new InvalidOperationException($"Invalid emoji: {emoji}");
                }
            }
        }

        try
        {
            // At least the first pair is required
            TryAdd(role1, emoji1);
            TryAdd(role2, emoji2);
            TryAdd(role3, emoji3);
            TryAdd(role4, emoji4);
            TryAdd(role5, emoji5);
            TryAdd(role6, emoji6);
            TryAdd(role7, emoji7);
            TryAdd(role8, emoji8);
            TryAdd(role9, emoji9);
            TryAdd(role10, emoji10);

            if (pairs.Count == 0)
                throw new InvalidOperationException("Provide at least one (role, emoji) pair.");
            if (pairs.Select(p => p.Key).Distinct().Count() != pairs.Count)
                throw new InvalidOperationException("Duplicate emojis are not allowed.");
        }
        catch (Exception ex)
        {
            await RespondAsync($":warning: {ex.Message}", ephemeral: true);
            return;
        }

        // Build bilingual message
        var sb = new StringBuilder()
            .AppendLine("**Role Selection / Rollenwahl**")
            .AppendLine("EN: React with the emoji to get the role. Remove your reaction to remove the role.")
            .AppendLine("DE: Reagiere mit dem Emoji, um die Rolle zu erhalten. Entferne die Reaktion, um die Rolle zu entfernen.")
            .AppendLine();

        foreach (var p in pairs)
            sb.AppendLine($"{p.Raw} → {p.Role.Mention}");

        // Post and add reactions
        var msg = await channel.SendMessageAsync(sb.ToString());
        foreach (var p in pairs)
            await msg.AddReactionAsync(p.Emote);

        // Persist
        var entry = new ReactionRoleEntry
        {
            GuildId = Context.Guild.Id,
            ChannelId = channel.Id,
            MessageId = msg.Id,
            RemoveOnUnreact = removeOnUnreact,
            Pairs = pairs.Select(p => new ReactionRolePair(p.Key, p.Role.Id, p.Raw)).ToList()
        };
        await store.AddReactionRoleAsync(entry);

        await RespondAsync($":white_check_mark: Reaction-roles registered in {channel.Mention}.", ephemeral: true);
    }
}
