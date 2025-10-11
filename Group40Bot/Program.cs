using System.Linq;
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
- Bootstraps Generic Host.
- Adds console logging with UTC timestamps.
- Enriches event logging with guild/user context.
- Registers per-guild slash commands on Ready and on new guilds.
*/
var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(l => l
        .ClearProviders()
        .AddSimpleConsole(o =>
        {
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss 'UTC' ";
            o.UseUtcTimestamp = true;
            o.SingleLine = true;
        })
        .SetMinimumLevel(LogLevel.Information))
    .ConfigureAppConfiguration(cfg => cfg
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables())
    .ConfigureServices((ctx, s) =>
    {
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

        s.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()));

        s.AddSingleton<ISettingsStore, FileSettingsStore>();
        s.AddHostedService<TempVoiceService>();
        s.AddHostedService<ReactionRoleService>();
        s.AddHostedService<PresenceService>();
        s.AddHostedService<BotRunner>();
        s.AddHostedService<GiveawayService>();
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
    private static string Ts() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private static string GSummary(IGuild g) => $"{g.Name}({g.Id})";
    private string GuildsSummary() => client.Guilds.Count == 0 ? "<none>" : string.Join(", ", client.Guilds.Select(GSummary));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        client.Log += m => { log.LogInformation("[{Ts}] {Src} {Msg}", Ts(), m.Source, m.Message); return Task.CompletedTask; };
        interactions.Log += m => { log.LogInformation("[{Ts}] {Src} {Msg}", Ts(), m.Source, m.Message); return Task.CompletedTask; };

        client.Connected += () => { log.LogInformation("[{Ts}] Connected", Ts()); return Task.CompletedTask; };
        client.Disconnected += ex =>
        {
            switch (ex)
            {
                case Discord.Net.WebSocketClosedException wse:
                    log.LogError("[{Ts}] Disconnected: CloseCode={Code} Reason={Reason}", Ts(), wse.CloseCode, wse.Reason);
                    break;
                default:
                    log.LogError(ex, "[{Ts}] Disconnected: {Type}", Ts(), ex?.GetType().Name ?? "unknown");
                    break;
            }
            return Task.CompletedTask;
        };

        client.GuildAvailable += g => { log.LogInformation("[{Ts}] Guild available: {G}", Ts(), GSummary(g)); return Task.CompletedTask; };
        client.GuildUnavailable += g => { log.LogWarning("[{Ts}] Guild unavailable: {G}", Ts(), GSummary(g)); return Task.CompletedTask; };
        client.JoinedGuild += g => { log.LogInformation("[{Ts}] Joined guild: {G}", Ts(), GSummary(g)); return RegisterForGuildAsync(g.Id); };
        client.LeftGuild += g => { log.LogInformation("[{Ts}] Left guild: {Id}", Ts(), g.Id); return Task.CompletedTask; };

        client.InteractionCreated += async inter =>
        {
            var g = inter.GuildId.HasValue ? client.GetGuild(inter.GuildId.Value) : null;
            log.LogInformation("[{Ts}] Interaction created type:{Type} in:{Guild} by:{User}",
                Ts(), inter.Type, g is null ? "DM" : GSummary(g), inter.User?.Id);
            var ctx = new SocketInteractionContext(client, inter);
            await interactions.ExecuteCommandAsync(ctx, services);
        };

        interactions.SlashCommandExecuted += (info, ctx, result) =>
        {
            var gtxt = ctx.Guild is null ? "DM" : GSummary(ctx.Guild);
            if (!result.IsSuccess)
                log.LogError("[{Ts}] Slash error {Cmd}: {Error} {Reason} by:{User} in:{Guild}",
                    Ts(), info.Name, result.Error, result.ErrorReason, ctx.User?.Id, gtxt);
            else
                log.LogInformation("[{Ts}] Slash ok {Cmd} by:{User} in:{Guild}",
                    Ts(), info.Name, ctx.User?.Id, gtxt);
            return Task.CompletedTask;
        };

        await interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), services);

        var token = cfg["DISCORD_TOKEN"] ?? throw new InvalidOperationException("DISCORD_TOKEN missing.");
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        client.Ready += OnReady;

        try { await Task.Delay(Timeout.Infinite, ct); } catch { }
    }

    private async Task OnReady()
    {
        try
        {
            foreach (var g in client.Guilds)
                await RegisterForGuildAsync(g.Id);

            log.LogInformation("[{Ts}] Ready. Guilds:{Count} [{List}]", Ts(), client.Guilds.Count, GuildsSummary());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Ts}] Ready handler failed", Ts());
        }
    }

    private Task RegisterForGuildAsync(ulong guildId)
        => interactions.RegisterCommandsToGuildAsync(guildId);

    public override async Task StopAsync(CancellationToken ct)
    {
        log.LogInformation("[{Ts}] Stopping...", Ts());
        await client.StopAsync();
        await client.LogoutAsync();
        await base.StopAsync(ct);
        log.LogInformation("[{Ts}] Stopped.", Ts());
    }
}
