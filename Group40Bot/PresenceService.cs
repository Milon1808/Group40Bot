using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Group40Bot;

/// <summary>
/// Rotates bot activity text at a fixed interval. Reads all values from the
/// "Presence" section to avoid key-resolution issues and logs the raw value.
/// </summary>
public sealed class PresenceService(
    DiscordSocketClient client,
    IConfiguration cfg,
    ILogger<PresenceService> log) : BackgroundService
{
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
            log.LogInformation("PresenceService disabled: no messages configured.");
            return;
        }
        log.LogInformation("PresenceService start: {Count} messages, interval {Interval}s (raw: {Raw})",
            messages.Length, intervalSec, raw ?? "<null>");

        // Wait for Ready once
        if (client.ConnectionState != ConnectionState.Connected)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task OnReady() { tcs.TrySetResult(true); return Task.CompletedTask; }
            client.Ready += OnReady;
            try { await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30), stoppingToken)); }
            finally { client.Ready -= OnReady; }
        }

        // Set status once
        try { await client.SetStatusAsync(UserStatus.Online); } catch { /* ignore */ }

        // Initial set + timer loop
        var i = 0;
        async Task SetOnce()
        {
            var text = messages[i++ % messages.Length];
            await client.SetGameAsync(text, type: ActivityType.Playing);
            log.LogInformation("Presence set: {Text}", text);
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
            catch (Exception ex) { log.LogDebug(ex, "Presence rotation tick failed"); }
        }
    }
}
