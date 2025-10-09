using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Admin-only announcement commands.
/// Logs with UTC timestamp + guild/channel context and reports clear, ephemeral errors to the invoking admin.
/// </summary>
[Group("announce", "Post announcements to a channel")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class AnnounceModule(ILogger<AnnounceModule> log) : InteractionModuleBase<SocketInteractionContext>
{
    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    // --- Logging helpers (UTC timestamp + context) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private (string ts, string gname, ulong gid, string uname, ulong uid) CtxBase()
        => (Ts(), Context.Guild?.Name ?? "<no-guild>", Context.Guild?.Id ?? 0UL, Context.User?.Username ?? "<user>", Context.User?.Id ?? 0UL);

    [SlashCommand("post", "Post an announcement to a channel")]
    public async Task PostAsync(
        SocketTextChannel channel,
        string message,
        string? title = null,
        SocketRole? role1 = null,
        SocketRole? role2 = null,
        SocketRole? role3 = null,
        SocketRole? role4 = null,
        SocketRole? role5 = null,
        bool everyone = false)
    {
        var (ts0, gname0, gid0, uname0, uid0) = CtxBase();
        log.LogInformation("[{Ts}] announce/post begin guild:{GName}({GId}) by:{User}({Uid}) -> target:{Chan}({Cid})",
            ts0, gname0, gid0, uname0, uid0, channel?.Name ?? "<null>", channel?.Id ?? 0UL);

        try
        {
            if (!IsAdmin())
            {
                await RespondAsync(":warning: You need **Administrator** permission to use this command.", ephemeral: true);
                return;
            }
            if (channel == null)
            {
                await RespondAsync(":warning: Target channel not found.", ephemeral: true);
                return;
            }

            var me = Context.Guild.CurrentUser;
            var perms = me.GetPermissions(channel);

            // Log permission snapshot with timestamp and guild
            var (tsP, gnameP, gidP, _, _) = CtxBase();
            log.LogInformation("[{Ts}] announce/post perms guild:{GName}({GId}) chan:{Chan}({Cid}) View={View} Send={Send} EmbedLinks={Embed} MentionEveryone={Mention}",
                tsP, gnameP, gidP, channel.Name, channel.Id, perms.ViewChannel, perms.SendMessages, perms.EmbedLinks, perms.MentionEveryone);

            // Build precise admin-facing error if perms missing
            if (!perms.ViewChannel || !perms.SendMessages)
            {
                await RespondAsync($":warning: Cannot post in {channel.Mention}.\n• Required: **View Channel**, **Send Messages**\n• Guild: **{gname0}** (`{gid0}`)\n• Time: `{ts0}`",
                    ephemeral: true);
                return;
            }
            if (everyone && !perms.MentionEveryone)
            {
                await RespondAsync($":warning: Missing **Mention Everyone** in {channel.Mention}.\n• Guild: **{gname0}** (`{gid0}`)\n• Time: `{ts0}`",
                    ephemeral: true);
                return;
            }

            // Normalize title/message (defensive caps)
            title = string.IsNullOrWhiteSpace(title) ? "Announcement" : title.Trim();
            if (title.Length > 256) title = title[..256];
            if (message.Length > 4000) message = message[..4000];

            // Build role allow-list
            var roles = new List<SocketRole>();
            void Add(SocketRole? r) { if (r != null) roles.Add(r); }
            Add(role1); Add(role2); Add(role3); Add(role4); Add(role5);

            // AllowedMentions: only explicit RoleIds + optional Everyone (do NOT set Roles flag together with RoleIds)
            var allowed = new AllowedMentions { AllowedTypes = AllowedMentionTypes.None };
            if (roles.Count > 0) allowed.RoleIds.AddRange(roles.Select(r => r.Id));
            if (everyone) allowed.AllowedTypes |= AllowedMentionTypes.Everyone;

            // Header with mentions (rendered text; pings governed by AllowedMentions)
            var header = roles.Count > 0 ? string.Join(" ", roles.Select(r => r.Mention)) : string.Empty;
            if (everyone) header = string.IsNullOrEmpty(header) ? "@everyone" : header + " @everyone";
            var headerOut = string.IsNullOrWhiteSpace(header) ? null : header;

            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(message)
                .WithColor(new Color(0x5865F2))
                .WithFooter($"by {Context.User.Username}")
                .WithCurrentTimestamp()
                .Build();

            // Log send intent with timestamp and full context
            var (ts1, gname1, gid1, uname1, uid1) = CtxBase();
            log.LogInformation("[{Ts}] announce/post send guild:{GName}({GId}) chan:{Chan}({Cid}) title:'{Title}' roles:{RoleCount} everyone:{Everyone} by:{User}({Uid})",
                ts1, gname1, gid1, channel.Name, channel.Id, title, roles.Count, everyone, uname1, uid1);

            await channel.SendMessageAsync(text: headerOut, embed: embed, allowedMentions: allowed);

            await RespondAsync($":white_check_mark: Announcement posted to {channel.Mention}.", ephemeral: true);

            var (ts2, gname2, gid2, _, _) = CtxBase();
            log.LogInformation("[{Ts}] announce/post ok guild:{GName}({GId}) chan:{Chan}({Cid})", ts2, gname2, gid2, channel.Name, channel.Id);
        }
        catch (Discord.Net.HttpException ex)
        {
            var (tsE, gnameE, gidE, _, _) = CtxBase();
            log.LogError(ex, "[{Ts}] announce/post HttpException http={Http} code={Code} reason={Reason} guild:{GName}({GId}) chan:{Chan}({Cid})",
                tsE, ex.HttpCode, ex.DiscordCode, ex.Reason, gnameE, gidE, channel?.Name ?? "<null>", channel?.Id ?? 0UL);

            // Ephemeral, admin-friendly diagnostics
            await RespondAsync(
                $":warning: HTTP error {(int)ex.HttpCode}: **{ex.Reason}**\n" +
                $"• Guild: **{gnameE}** (`{gidE}`)\n" +
                $"• Channel: {(channel != null ? channel.Mention : "`<unknown>`")}\n" +
                $"• Time: `{tsE}`",
                ephemeral: true);
        }
        catch (Exception ex)
        {
            var (tsU, gnameU, gidU, _, _) = CtxBase();
            log.LogError(ex, "[{Ts}] announce/post failed guild:{GName}({GId}) chan:{Chan}({Cid})",
                tsU, gnameU, gidU, channel?.Name ?? "<null>", channel?.Id ?? 0UL);

            await RespondAsync(
                ":warning: Failed to send announcement. See logs for details.\n" +
                $"• Guild: **{gnameU}** (`{gidU}`)\n" +
                $"• Channel: {(channel != null ? channel.Mention : "`<unknown>`")}\n" +
                $"• Time: `{tsU}`",
                ephemeral: true);
        }
    }
}
