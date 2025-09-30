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
- Bootstraps a Generic Host with DI, logging and config.
- Wires DiscordSocketClient + InteractionService.
- BotRunner logs in, loads modules, registers slash-commands per guild on Ready and when joining new guilds.
- No TEST_GUILD_ID needed anymore; commands deploy instantly to all guilds the bot is in.
*/

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(cfg =>
    {
        cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
           .AddUserSecrets<Program>(optional: true)   // DISCORD_TOKEN can live here locally
           .AddEnvironmentVariables();                // and in production via env vars
    })
    .ConfigureLogging(l => l.ClearProviders().AddConsole())
    .ConfigureServices((ctx, s) =>
    {
        var intents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildVoiceStates;
        s.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false
        }));

        // InteractionService needs the client in its ctor
        s.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()));

        s.AddSingleton<ISettingsStore, FileSettingsStore>();
        s.AddHostedService<TempVoiceService>();
        s.AddHostedService<BotRunner>();
    })
    .Build();

await host.RunAsync();

public sealed class BotRunner(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IConfiguration cfg,
    ILogger<BotRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Logging hooks
        client.Log += m => { log.LogInformation("{Src} {Msg}", m.Source, m.Message); return Task.CompletedTask; };
        interactions.Log += m => { log.LogInformation("{Src} {Msg}", m.Source, m.Message); return Task.CompletedTask; };

        // Discord events
        client.Ready += OnReady;
        client.JoinedGuild += g => RegisterForGuildAsync(g.Id);
        client.InteractionCreated += async inter =>
        {
            var ctx = new SocketInteractionContext(client, inter);
            await interactions.ExecuteCommandAsync(ctx, services);
        };

        // Load all Interaction modules from this assembly
        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);

        // Login + start
        var token = cfg["DISCORD_TOKEN"] ?? throw new InvalidOperationException("DISCORD_TOKEN missing.");
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (TaskCanceledException) { }
    }

    private async Task OnReady()
    {
        try
        {
            // Register commands to every guild the bot is currently in
            foreach (var g in client.Guilds)
                await RegisterForGuildAsync(g.Id);

            log.LogInformation("Slash-commands registered for {Count} guild(s).", client.Guilds.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ready handler failed");
        }
    }

    private async Task RegisterForGuildAsync(ulong guildId)
    {
        // NOTE: Per-guild registration propagates instantly and keeps guilds independent.
        await interactions.RegisterCommandsToGuildAsync(guildId);
        log.LogInformation("Commands registered for guild {GuildId}", guildId);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopAsync();
        await client.LogoutAsync();
        await base.StopAsync(cancellationToken);
    }
}
