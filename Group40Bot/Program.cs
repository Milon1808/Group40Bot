using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Group40Bot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/*
SUMMARY (EN):
- Bootstraps a .NET Generic Host with DI, logging, and configuration.
- Registers DiscordSocketClient and InteractionService.
- Starts the bot via BotRunner; registers slash-commands per guild on Ready and when joining new guilds.
*/
var host = Host.CreateDefaultBuilder(args)
    // ===== LOGGING: console sink + minimum level =====
    .ConfigureLogging(l => l
        .ClearProviders()
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information))
    // ===== CONFIG: appsettings + UserSecrets + ENV =====
    .ConfigureAppConfiguration(cfg => cfg
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables())
    // ===== DEPENDENCY INJECTION / SERVICES =====
    .ConfigureServices((ctx, s) =>
    {
        // NOTE: Keep GuildMembers intent only if enabled in the Developer Portal.
        var intents = GatewayIntents.Guilds
                   | GatewayIntents.GuildMembers
                   | GatewayIntents.GuildVoiceStates
                   | GatewayIntents.GuildMessageReactions;

        s.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false
        }));

        // InteractionService requires the client in its ctor
        s.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()));

        s.AddSingleton<ISettingsStore, FileSettingsStore>();
        s.AddHostedService<TempVoiceService>();
        s.AddHostedService<ReactionRoleService>();
        s.AddHostedService<BotRunner>();
        s.AddHostedService<PresenceService>();   // periodic presence rotation
    })
    .Build();

await host.RunAsync();

/// <summary>
/// Owns Discord client lifecycle: hooks logging, loads modules,
/// handles interaction dispatching, and per-guild command registration.
/// </summary>
public sealed class BotRunner(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IConfiguration cfg,
    ILogger<BotRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // ===== LOGGING: base logs from Discord.Net & InteractionService =====
        client.Log += m => { log.LogInformation("{Src} {Msg}", m.Source, m.Message); return Task.CompletedTask; };
        interactions.Log += m => { log.LogInformation("{Src} {Msg}", m.Source, m.Message); return Task.CompletedTask; };

        // ===== LOGGING: connection diagnostics =====
        client.Connected += () => { log.LogInformation("Connected"); return Task.CompletedTask; };
        client.Disconnected += ex =>
        {
            switch (ex)
            {
                case Discord.Net.WebSocketClosedException wse:
                    log.LogError("Disconnected: CloseCode={Code} Reason={Reason}", wse.CloseCode, wse.Reason);
                    break;
                default:
                    log.LogError(ex, "Disconnected: {Type}", ex?.GetType().Name ?? "unknown");
                    break;
            }
            return Task.CompletedTask;
        };

        // ===== LOGGING: guild availability =====
        client.GuildAvailable += g => { log.LogInformation("Guild available: {Name} ({Id})", g.Name, g.Id); return Task.CompletedTask; };
        client.GuildUnavailable += g => { log.LogWarning("Guild unavailable: {Name} ({Id})", g.Name, g.Id); return Task.CompletedTask; };

        // ===== LOGGING: slash result =====
        interactions.SlashCommandExecuted += (info, ctx, result) =>
        {
            if (!result.IsSuccess)
                log.LogError("Slash error {Cmd}: {Error} {Reason}", info.Name, result.Error, result.ErrorReason);
            else
                log.LogInformation("Slash ok {Cmd} by {User} in {Guild}", info.Name, ctx.User?.Id, (ctx.Guild?.Id.ToString() ?? "DM"));
            return Task.CompletedTask;
        };

        // ===== EVENTS =====
        client.Ready += OnReady;
        client.JoinedGuild += g => RegisterForGuildAsync(g.Id);
        client.InteractionCreated += async inter =>
        {
            var ctx = new SocketInteractionContext(client, inter);
            await interactions.ExecuteCommandAsync(ctx, services);
        };

        // ===== MODULE DISCOVERY =====
        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);

        // ===== LOGIN / START =====
        var token = cfg["DISCORD_TOKEN"] ?? throw new InvalidOperationException("DISCORD_TOKEN missing.");
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        try { await Task.Delay(Timeout.Infinite, ct); } catch { /* ignore */ }
    }

    private async Task OnReady()
    {
        try
        {
            foreach (var g in client.Guilds)
                await RegisterForGuildAsync(g.Id);

            log.LogInformation("Slash-commands registered for {Count} guild(s).", client.Guilds.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ready handler failed");
        }
    }

    private Task RegisterForGuildAsync(ulong guildId)
        => interactions.RegisterCommandsToGuildAsync(guildId);

    public override async Task StopAsync(CancellationToken ct)
    {
        await client.StopAsync();
        await client.LogoutAsync();
        await base.StopAsync(ct);
    }
}
