using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Admin-only slash commands to schedule and manage giveaways.
/// Time parameters are parsed as local time "yyyy-MM-dd HH:mm" unless end with 'Z' (UTC).
/// </summary>
[Group("giveaway", "Schedule and manage giveaways")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[EnabledInDm(false)]
public sealed class GiveawayModule(ISettingsStore store, ILogger<GiveawayModule> log)
    : InteractionModuleBase<SocketInteractionContext>
{
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private bool IsAdmin() => Context.User is SocketGuildUser u && u.GuildPermissions.Administrator;

    private static bool TryParseWhen(string input, out DateTimeOffset dto)
    {
        // Accepts: "2025-12-24 18:30" (assume local), ISO, UTC with 'Z'
        var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;

        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, styles, out dto))
            return true;

        if (DateTimeOffset.TryParse(input, CultureInfo.CurrentCulture, styles, out dto))
            return true;

        if (DateTime.TryParseExact(input, "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        { dto = new DateTimeOffset(dt); return true; }

        return false;
    }

    [SlashCommand("schedule", "Schedule a giveaway with start/end, text and an emoji to join")]
    public async Task ScheduleAsync(
        SocketTextChannel channel,
        string title,
        string body,
        string start_time,
        string end_time,
        string emoji = "🎉",
        [Summary(description: "Roles to exclude from participation")] SocketRole? exclude1 = null,
        SocketRole? exclude2 = null,
        SocketRole? exclude3 = null,
        SocketRole? exclude4 = null,
        SocketRole? exclude5 = null,
        bool exclude_bots = true)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        if (!TryParseWhen(start_time, out var start)) { await RespondAsync(":warning: Invalid start_time. Use e.g. `2025-12-24 18:30`.", ephemeral: true); return; }
        if (!TryParseWhen(end_time, out var end)) { await RespondAsync(":warning: Invalid end_time. Use e.g. `2025-12-24 20:00`.", ephemeral: true); return; }
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        if (end <= start) { await RespondAsync(":warning: end_time must be after start_time.", ephemeral: true); return; }

        var me = Context.Guild.CurrentUser;
        var perms = me.GetPermissions(channel);
        if (!perms.ViewChannel || !perms.SendMessages || !perms.AddReactions)
        { await RespondAsync(":warning: Bot lacks required channel permissions (View/Send/Add Reactions).", ephemeral: true); return; }

        var gw = new GiveawayEntry
        {
            GuildId = Context.Guild.Id,
            ChannelId = channel.Id,
            Title = title.Trim(),
            Body = body.Trim(),
            StartUtc = start,
            EndUtc = end,
            ExcludeBots = exclude_bots
        };

        if (Emote.TryParse(emoji, out var custom))
        { gw.EmojiKey = custom.Id.ToString(); gw.EmojiRaw = custom.ToString(); }
        else
        { gw.EmojiKey = emoji; gw.EmojiRaw = emoji; }

        void AddEx(SocketRole? r) { if (r != null) gw.ExcludedRoleIds.Add(r.Id); }
        AddEx(exclude1); AddEx(exclude2); AddEx(exclude3); AddEx(exclude4); AddEx(exclude5);

        await store.AddOrUpdateGiveawayAsync(gw);

        var when = $"Starts: <t:{gw.StartUtc.ToUnixTimeSeconds()}:F>\nEnds: <t:{gw.EndUtc.ToUnixTimeSeconds()}:F>";
        await RespondAsync($":white_check_mark: Scheduled giveaway `{gw.Title}` in {channel.Mention}.\n{when}\nID: `{gw.Id}`\nJoin emoji: {gw.EmojiRaw}",
            ephemeral: true);

        log.LogInformation("[{Ts}] gw/schedule ok guild:{G} chan:{C} id:{Id} start:{S:u} end:{E:u}",
            Ts(), Context.Guild.Id, channel.Id, gw.Id, gw.StartUtc, gw.EndUtc);
    }

    [SlashCommand("list", "List scheduled/active giveaways (optionally include completed)")]
    public async Task ListAsync(bool include_completed = false)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }

        var items = await store.ListGiveawaysAsync(Context.Guild.Id);
        var view = include_completed
            ? items
            : items.Where(g => g.Status == GiveawayStatus.Scheduled || g.Status == GiveawayStatus.Active).ToList();

        if (view.Count == 0)
        {
            var note = include_completed ? "_none_" : "_none (scheduled/active)_";
            await RespondAsync(note, ephemeral: false);
            return;
        }

        var eb = new EmbedBuilder().WithTitle("Giveaways").WithColor(new Color(0xFEE75C));

        var lines = view.Select(g =>
        {
            var time = $"<t:{g.StartUtc.ToUnixTimeSeconds()}:f> → <t:{g.EndUtc.ToUnixTimeSeconds()}:f>";
            var msg = g.MessageId == 0 ? "-" : g.MessageId.ToString();
            var winner = g.WinnerUserId.HasValue ? $" winner:{g.WinnerUserId.Value}" : "";
            return $"`{g.Id}` • **{g.Title}** • {g.Status} • {time} • chan:{g.ChannelId} msg:{msg}{winner}";
        });

        eb.WithDescription("```\n" + string.Join("\n", lines) + "\n```");
        await RespondAsync(embed: eb.Build(), ephemeral: false);
    }


    [SlashCommand("cancel", "Cancel a scheduled or active giveaway by ID")]
    public async Task CancelAsync(string id)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        if (!Guid.TryParse(id, out var gid)) { await RespondAsync(":warning: Provide a valid GUID.", ephemeral: true); return; }

        var gw = await store.GetGiveawayByIdAsync(Context.Guild.Id, gid);
        if (gw is null) { await RespondAsync(":warning: Giveaway not found.", ephemeral: true); return; }

        if (gw.Status == GiveawayStatus.Completed || gw.Status == GiveawayStatus.Cancelled)
        { await RespondAsync(":warning: Already completed or cancelled.", ephemeral: true); return; }

        gw.Status = GiveawayStatus.Cancelled;
        await store.AddOrUpdateGiveawayAsync(gw);
        await RespondAsync($":white_check_mark: Cancelled `{gw.Title}` (ID `{gw.Id}`).", ephemeral: true);
    }

    [SlashCommand("reroll", "Pick a new winner for a completed giveaway by ID")]
    public async Task RerollAsync(string id)
    {
        if (!IsAdmin()) { await RespondAsync("Insufficient permissions.", ephemeral: true); return; }
        if (!Guid.TryParse(id, out var gid)) { await RespondAsync(":warning: Provide a valid GUID.", ephemeral: true); return; }

        var gw = await store.GetGiveawayByIdAsync(Context.Guild.Id, gid);
        if (gw is null || gw.Status != GiveawayStatus.Completed)
        { await RespondAsync(":warning: Giveaway not found or not completed.", ephemeral: true); return; }

        var guild = (SocketGuild)Context.Guild;

        // Eligibility wie im Service
        var eligible = new List<ulong>();
        foreach (var uid in gw.ParticipantIds)
        {
            var u = guild.GetUser(uid);
            if (u is null) continue;
            if (gw.ExcludeBots && (u.IsBot || u.IsWebhook)) continue;
            if (u.Roles.Any(r => gw.ExcludedRoleIds.Contains(r.Id))) continue;
            eligible.Add(uid);
        }

        if (eligible.Count == 0) { await RespondAsync(":warning: No eligible participants.", ephemeral: true); return; }

        // Secure random winner
        var idx = System.Security.Cryptography.RandomNumberGenerator.GetInt32(eligible.Count);
        var winner = eligible[idx];

        var channel = guild.GetTextChannel(gw.ChannelId);
        if (channel is null) { await RespondAsync(":warning: Channel missing.", ephemeral: true); return; }

        var eb = new EmbedBuilder()
            .WithTitle($"🎉 Reroll Winner: <@{winner}>")
            .WithDescription($"{gw.Body}\n\nCongratulations <@{winner}>!")
            .WithColor(new Color(0xFEE75C))
            .WithFooter($"Giveaway ID: {gw.Id}")
            .Build();

        var allowed = new AllowedMentions { AllowedTypes = AllowedMentionTypes.None };
        allowed.UserIds.Add(winner);

        await channel.SendMessageAsync(text: $"<@{winner}>", embed: eb, allowedMentions: allowed);
        await RespondAsync($":white_check_mark: Rerolled in {channel.Mention}.", ephemeral: true);
    }
}
