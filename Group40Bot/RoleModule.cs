using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Group40Bot;

/// <summary>
/// Slash command group for self-assignable roles via reactions.
/// Admin-only. Registers a message with role ↔ emoji mappings,
/// handles unregister and list. Output is formatted as an embed
/// and role mentions are rendered without pinging.
/// </summary>
[Group("role", "Self-assign roles via reactions")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class RoleModule(ISettingsStore store) : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>Runtime guard. Avoids accidental execution if Discord perms are misconfigured.</summary>
    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    /// <summary>
    /// Create a reaction-roles message in a target channel.
    /// Accepts up to 10 (role, emoji) pairs. Emojis can be unicode or custom <:name:id:>.
    /// The bot must be able to manage the provided roles (role below bot's top role).
    /// </summary>
    [SlashCommand("register", "Create a reaction-roles message in a channel")]
    public async Task Register(
        SocketTextChannel channel,
        SocketRole role1, string emoji1,
        SocketRole? role2 = null, string? emoji2 = null,
        SocketRole? role3 = null, string? emoji3 = null,
        SocketRole? role4 = null, string? emoji4 = null,
        SocketRole? role5 = null, string? emoji5 = null,
        SocketRole? role6 = null, string? emoji6 = null,
        SocketRole? role7 = null, string? emoji7 = null,
        SocketRole? role8 = null, string? emoji8 = null,
        SocketRole? role9 = null, string? emoji9 = null,
        SocketRole? role10 = null, string? emoji10 = null,
        bool remove_on_unreact = true)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

        var me = Context.Guild.CurrentUser;
        if (!me.GuildPermissions.ManageRoles)
        { await RespondAsync("Bot is missing **Manage Roles**.", ephemeral: true); return; }

        // Build (role, emote, key, raw) tuples
        var pairs = new List<(SocketRole Role, IEmote Emote, string Key, string Raw)>();

        void Add(SocketRole? role, string? emoji)
        {
            if (role == null || string.IsNullOrWhiteSpace(emoji)) return;
            if (role.Position >= me.Hierarchy)
                throw new InvalidOperationException($"Bot role must be higher than @{role.Name}.");

            // Custom emote or unicode emoji
            if (Emote.TryParse(emoji, out var custom))
                pairs.Add((role, custom, custom.Id.ToString(), custom.ToString()));
            else
                pairs.Add((role, new Emoji(emoji), emoji, emoji));
        }

        try
        {
            Add(role1, emoji1); Add(role2, emoji2); Add(role3, emoji3); Add(role4, emoji4); Add(role5, emoji5);
            Add(role6, emoji6); Add(role7, emoji7); Add(role8, emoji8); Add(role9, emoji9); Add(role10, emoji10);

            if (pairs.Count == 0)
                throw new InvalidOperationException("Provide at least one (role, emoji) pair.");

            // No duplicate emojis
            if (pairs.Select(p => p.Key).Distinct().Count() != pairs.Count)
                throw new InvalidOperationException("Duplicate emojis are not allowed.");
        }
        catch (Exception ex)
        {
            await RespondAsync($":warning: {ex.Message}", ephemeral: true);
            return;
        }

        // ----- Message body WITHOUT code block so role mentions render as @Role -----
        var desc = new StringBuilder()
            .AppendLine("**Role Selection / Rollenwahl**")
            .AppendLine("EN: React with the emoji to get the role. Remove your reaction to remove the role.")
            .AppendLine("DE: Reagiere mit dem Emoji, um die Rolle zu erhalten. Entferne die Reaktion, um die Rolle zu entfernen.")
            .AppendLine();

        foreach (var p in pairs)
            desc.AppendLine($"{p.Raw} → {p.Role.Mention}");

        var embed = new EmbedBuilder()
            .WithTitle("Reaction Roles")
            .WithDescription(desc.ToString())
            .WithColor(new Color(0x57F287))
            .Build();

        // IMPORTANT: do not ping roles while still rendering as mentions
        var msg = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);

        // Add reactions for each mapping
        foreach (var p in pairs)
            await msg.AddReactionAsync(p.Emote);

        // Persist mapping for runtime handler
        var entry = new ReactionRoleEntry
        {
            GuildId = Context.Guild.Id,
            ChannelId = channel.Id,
            MessageId = msg.Id,
            RemoveOnUnreact = remove_on_unreact,
            Pairs = pairs.Select(p => new ReactionRolePair(p.Key, p.Role.Id, p.Raw)).ToList()
        };
        await store.AddReactionRoleAsync(entry);

        await RespondAsync($":white_check_mark: Registered in {channel.Mention}.", ephemeral: true);
    }

    /// <summary>Remove a reaction-roles message by link or message ID.</summary>
    [SlashCommand("unregister", "Disable a reaction-roles message by link or ID")]
    public async Task Unregister(string message_link_or_id)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        if (!TryParseIds(message_link_or_id, out var channelId, out var messageId))
        { await RespondAsync(":warning: Provide a message link or ID.", ephemeral: true); return; }

        await store.RemoveReactionRoleAsync(Context.Guild.Id, messageId);
        await RespondAsync($":white_check_mark: Unregistered message `{messageId}`.", ephemeral: true);
    }

    /// <summary>List all reaction-role registrations for this guild.</summary>
    [SlashCommand("list", "List all reaction-role messages")]
    public async Task List()
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        var items = await store.ListReactionRolesAsync(Context.Guild.Id);
        if (items.Count == 0) { await RespondAsync("_none_", ephemeral: true); return; }

        var sb = new StringBuilder().AppendLine("```");
        foreach (var e in items)
            sb.AppendLine($"channel:{e.ChannelId} message:{e.MessageId} pairs:{e.Pairs.Count}");
        sb.AppendLine("```");

        var embed = new EmbedBuilder()
            .WithTitle("Reaction Roles")
            .WithDescription(sb.ToString())
            .WithColor(new Color(0xFEE75C))
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }

    /// <summary>
    /// Parse a Discord message link or a plain message ID.
    /// Returns channelId if present in the link; messageId is required.
    /// </summary>
    private static bool TryParseIds(string input, out ulong channelId, out ulong messageId)
    {
        channelId = 0; messageId = 0;
        // Format: https://discord.com/channels/<guild>/<channel>/<message>
        var parts = input.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && ulong.TryParse(parts[^1], out messageId))
        {
            _ = ulong.TryParse(parts[^2], out channelId);
            return true;
        }
        return ulong.TryParse(input, out messageId);
    }
}
