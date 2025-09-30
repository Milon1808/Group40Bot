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
- Boots the Generic Host and wires Discord client + interactions.
- Registers slash-commands for every guild on Ready and when joining new guilds.
- Adds ReactionRoleService and enables GuildMessageReactions intent.
*/

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(cfg =>
    {
        cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
           .AddUserSecrets<Program>(optional: true)
           .AddEnvironmentVariables();
    })
    .ConfigureLogging(l => l.ClearProviders().AddConsole())
    .ConfigureServices((ctx, s) =>
    {
        var intents =
            GatewayIntents.Guilds |
            GatewayIntents.GuildMembers |
            GatewayIntents.GuildVoiceStates |
            GatewayIntents.GuildMessageReactions; // NEW

        s.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false
        }));

        s.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()));

        s.AddSingleton<ISettingsStore, FileSettingsStore>();
        s.AddHostedService<TempVoiceService>();
        s.AddHostedService<ReactionRoleService>();
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
        client.Log += m => { log.LogInformation("{Src} {Msg}", m.Source, m.Message); return Task.CompletedTask; };
        interactions.Log += m => { log.LogInformation("{Src} {Msg}", m.Source, m.Message); return Task.CompletedTask; };

        client.Ready += OnReady;
        client.JoinedGuild += g => RegisterForGuildAsync(g.Id);
        client.InteractionCreated += async inter =>
        {
            var ctx = new SocketInteractionContext(client, inter);
            await interactions.ExecuteCommandAsync(ctx, services);
        };

        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);

        var token = cfg["DISCORD_TOKEN"] ?? throw new InvalidOperationException("DISCORD_TOKEN missing.");
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (TaskCanceledException) { }
    }

    private async Task OnReady()
    {
        foreach (var g in client.Guilds)
            await RegisterForGuildAsync(g.Id);
        // log handled by interactions.Log
    }

    private async Task RegisterForGuildAsync(ulong guildId)
        => await interactions.RegisterCommandsToGuildAsync(guildId);

    public override async Task StopAsync(CancellationToken ct)
    {
        await client.StopAsync();
        await client.LogoutAsync();
        await base.StopAsync(ct);
    }
}
