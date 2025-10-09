using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Rotates bot activity text at a fixed interval. Reads all values from the
/// "Presence" section and logs detailed context with UTC timestamps.
/// </summary>
public sealed class PresenceService(
    DiscordSocketClient client,
    IConfiguration cfg,
    ILogger<PresenceService> log) : BackgroundService
{
    // --- Logging helpers (UTC timestamp) ---
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var presence = cfg.GetSection("Presence");

        // Messages
        var messages = presence.GetSection("Messages").Get<string[]>() ??
                       new[] { "Powered by Group40", "Use /help", "Temp voice channels active", "Reaction roles: /role register" };

        // Interval
        var raw = presence["IntervalSeconds"]; // raw string for logging
        var intervalSec = Math.Max(15, presence.GetValue<int>("IntervalSeconds", 180));

        if (messages.Length == 0)
        {
            log.LogInformation("[{Ts}] PresenceService disabled: no messages configured.", Ts());
            return;
        }

        log.LogInformation("[{Ts}] PresenceService start: {Count} messages, interval {Interval}s (raw: {Raw})",
            Ts(), messages.Length, intervalSec, raw ?? "<null>");

        // Wait for Ready once
        if (client.ConnectionState != ConnectionState.Connected)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task OnReady() { tcs.TrySetResult(true); return Task.CompletedTask; }
            client.Ready += OnReady;
            try
            {
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30), stoppingToken));
            }
            finally
            {
                client.Ready -= OnReady;
            }
        }

        // Log guild context once we are ready (if available)
        var guildCount = client.Guilds.Count;
        var guildIds = guildCount > 0 ? string.Join(",", client.Guilds.Select(g => g.Id)) : "<none>";
        log.LogInformation("[{Ts}] PresenceService client ready. Guilds:{Count} Ids:{Ids}", Ts(), guildCount, guildIds);

        // Set status once
        try { await client.SetStatusAsync(UserStatus.Online); } catch (Exception ex) { log.LogDebug(ex, "[{Ts}] SetStatus failed", Ts()); }

        // Initial set + timer loop
        var i = 0;
        async Task SetOnce()
        {
            var text = messages[i++ % messages.Length];
            try
            {
                await client.SetGameAsync(text, type: ActivityType.Playing);
                log.LogInformation("[{Ts}] Presence set: {Text}", Ts(), text);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "[{Ts}] Presence update failed for text: {Text}", Ts(), text);
            }
        }

        await SetOnce();

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSec));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await SetOnce();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "[{Ts}] Presence rotation tick failed", Ts());
            }
        }

        log.LogInformation("[{Ts}] PresenceService stopping", Ts());
    }
}
