using System.Text;
using System.Linq;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Self-assign roles via reactions. Admin-only.
/// Adds structured logging with UTC timestamps and guild/user/channel context.
/// Non-list commands reply ephemeral; list is public.
/// </summary>
[Group("role", "Self-assign roles via reactions")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class RoleModule(ISettingsStore store, ILogger<RoleModule> log) : InteractionModuleBase<SocketInteractionContext>
{
    // --- Logging helpers (UTC timestamp + context) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private (string ts, string gname, ulong gid, string uname, ulong uid) CtxBase()
        => (Ts(), Context.Guild?.Name ?? "<no-guild>", Context.Guild?.Id ?? 0UL,
            Context.User?.Username ?? "<user>", Context.User?.Id ?? 0UL);

    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    /// <summary>
    /// Create a reaction-roles message and store mapping.
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
        var (ts0, gname0, gid0, uname0, uid0) = CtxBase();
        log.LogInformation("[{Ts}] role/register begin guild:{GName}({GId}) chan:{Chan}({Cid}) by:{User}({Uid})",
            ts0, gname0, gid0, channel?.Name ?? "<null>", channel?.Id ?? 0UL, uname0, uid0);

        try
        {
            if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

            var me = Context.Guild.CurrentUser;
            if (!me.GuildPermissions.ManageRoles)
            { await RespondAsync("Bot is missing **Manage Roles**.", ephemeral: true); return; }

            var pairs = new List<(SocketRole Role, IEmote Emote, string Key, string Raw)>();
            void Add(SocketRole? role, string? emoji)
            {
                if (role == null || string.IsNullOrWhiteSpace(emoji)) return;
                if (role.Position >= me.Hierarchy)
                    throw new InvalidOperationException($"Bot role must be higher than @{role.Name}.");

                if (Emote.TryParse(emoji, out var custom))
                    pairs.Add((role, custom, custom.Id.ToString(), custom.ToString()));
                else
                    pairs.Add((role, new Emoji(emoji), emoji, emoji));
            }

            try
            {
                Add(role1, emoji1); Add(role2, emoji2); Add(role3, emoji3); Add(role4, emoji4); Add(role5, emoji5);
                Add(role6, emoji6); Add(role7, emoji7); Add(role8, emoji8); Add(role9, emoji9); Add(role10, emoji10);

                if (pairs.Count == 0) throw new InvalidOperationException("Provide at least one (role, emoji) pair.");
                if (pairs.Select(p => p.Key).Distinct().Count() != pairs.Count) throw new InvalidOperationException("Duplicate emojis are not allowed.");
            }
            catch (Exception ex)
            {
                log.LogInformation("[{Ts}] role/register validate-failed guild:{GId} reason:{Reason}", Ts(), gid0, ex.Message);
                await RespondAsync($":warning: {ex.Message}", ephemeral: true);
                return;
            }

            // Human-readable description (mentions render as @Role); avoid pings via AllowedMentions.None on send.
            var desc = new StringBuilder()
                .AppendLine("**Role Selection / Rollenwahl**")
                .AppendLine("EN: React with the emoji to get the role. Remove your reaction to remove the role.")
                .AppendLine("DE: Reagiere mit dem Emoji, um die Rolle zu erhalten. Entferne die Reaktion, um die Rolle zu entfernen.")
                .AppendLine();
            foreach (var p in pairs) desc.AppendLine($"{p.Raw} → {p.Role.Mention}");

            var embed = new EmbedBuilder()
                .WithTitle("Reaction Roles")
                .WithDescription(desc.ToString())
                .WithColor(new Color(0x57F287))
                .Build();

            log.LogInformation("[{Ts}] role/register send-message guild:{GId} chan:{Cid} pairs:{Count}",
                Ts(), gid0, channel.Id, pairs.Count);

            IUserMessage msg;
            try
            {
                msg = await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
            }
            catch (Discord.Net.HttpException ex)
            {
                log.LogError(ex, "[{Ts}] role/register HttpException on send http={Http} code={Code} reason={Reason} guild:{GId} chan:{Cid}",
                    Ts(), ex.HttpCode, ex.DiscordCode, ex.Reason, gid0, channel.Id);
                await RespondAsync($":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**", ephemeral: true);
                return;
            }

            // Add reactions best-effort
            foreach (var p in pairs)
            {
                try { await msg.AddReactionAsync(p.Emote); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "[{Ts}] role/register add-reaction failed guild:{GId} msg:{Msg} emote:{Emote}",
                        Ts(), gid0, msg.Id, p.Raw);
                }
            }

            var entry = new ReactionRoleEntry
            {
                GuildId = Context.Guild.Id,
                ChannelId = channel.Id,
                MessageId = msg.Id,
                RemoveOnUnreact = remove_on_unreact,
                Pairs = pairs.Select(p => new ReactionRolePair(p.Key, p.Role.Id, p.Raw)).ToList()
            };

            try
            {
                await store.AddReactionRoleAsync(entry);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[{Ts}] role/register store-write failed guild:{GId} msg:{Msg}", Ts(), gid0, msg.Id);
                await RespondAsync(":warning: Stored mapping failed. See logs.", ephemeral: true);
                return;
            }

            await RespondAsync($":white_check_mark: Registered in {channel.Mention}.", ephemeral: true);
            log.LogInformation("[{Ts}] role/register ok guild:{GId} chan:{Cid} msg:{Msg}", Ts(), gid0, channel.Id, msg.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] role/register failed guild:{GId} chan:{Cid}", Ts(), gid0, channel?.Id ?? 0UL);
            await RespondAsync(":warning: Failed to register reaction roles. See logs for details.", ephemeral: true);
        }
    }

    /// <summary>
    /// Remove a stored reaction-role mapping by link or message ID.
    /// </summary>
    [SlashCommand("unregister", "Disable a reaction-roles message by link or ID")]
    public async Task Unregister(string message_link_or_id)
    {
        var (ts0, gname0, gid0, uname0, uid0) = CtxBase();
        log.LogInformation("[{Ts}] role/unregister begin guild:{GName}({GId}) by:{User}({Uid}) input:{Input}",
            ts0, gname0, gid0, uname0, uid0, message_link_or_id);

        try
        {
            if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

            if (!TryParseIds(message_link_or_id, out var channelId, out var messageId))
            { await RespondAsync(":warning: Provide a message link or ID.", ephemeral: true); return; }

            await store.RemoveReactionRoleAsync(Context.Guild.Id, messageId);
            await RespondAsync($":white_check_mark: Unregistered message `{messageId}`.", ephemeral: true);

            log.LogInformation("[{Ts}] role/unregister ok guild:{GId} msg:{Msg} parsedChannel:{Ch}",
                Ts(), gid0, messageId, channelId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] role/unregister failed guild:{GId}", Ts(), gid0);
            await RespondAsync(":warning: Failed to unregister. See logs for details.", ephemeral: true);
        }
    }

    /// <summary>
    /// List all reaction-role mappings for this guild.
    /// </summary>
    [SlashCommand("list", "List all reaction-role messages")]
    public async Task List()
    {
        var (ts0, gname0, gid0, uname0, uid0) = CtxBase();
        log.LogInformation("[{Ts}] role/list begin guild:{GName}({GId}) by:{User}({Uid})",
            ts0, gname0, gid0, uname0, uid0);

        try
        {
            if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

            var items = await store.ListReactionRolesAsync(Context.Guild.Id);
            if (items.Count == 0)
            {
                await RespondAsync("_none_", ephemeral: false);
                log.LogInformation("[{Ts}] role/list ok guild:{GId} count:0", Ts(), gid0);
                return;
            }

            var sb = new StringBuilder().AppendLine("```");
            foreach (var e in items)
                sb.AppendLine($"channel:{e.ChannelId} message:{e.MessageId} pairs:{e.Pairs.Count}");
            sb.AppendLine("```");

            var embed = new EmbedBuilder()
                .WithTitle("Reaction Roles")
                .WithDescription(sb.ToString())
                .WithColor(new Color(0xFEE75C))
                .Build();

            await RespondAsync(embed: embed, ephemeral: false);
            log.LogInformation("[{Ts}] role/list ok guild:{GId} count:{Count}", Ts(), gid0, items.Count);
        }
        catch (Discord.Net.HttpException ex)
        {
            log.LogError(ex, "[{Ts}] role/list HttpException http={Http} code={Code} reason={Reason} guild:{GId}",
                Ts(), ex.HttpCode, ex.DiscordCode, ex.Reason, gid0);
            await RespondAsync($":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**", ephemeral: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] role/list failed guild:{GId}", Ts(), gid0);
            await RespondAsync(":warning: Failed to list reaction roles. See logs for details.", ephemeral: true);
        }
    }

    /// <summary>
    /// Try to extract channel and message IDs from a message link or a raw ID.
    /// </summary>
    private static bool TryParseIds(string input, out ulong channelId, out ulong messageId)
    {
        channelId = 0; messageId = 0;
        var parts = input.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && ulong.TryParse(parts[^1], out messageId))
        { _ = ulong.TryParse(parts[^2], out channelId); return true; }
        return ulong.TryParse(input, out messageId);
    }
}
