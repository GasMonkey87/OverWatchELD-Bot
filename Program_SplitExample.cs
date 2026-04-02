using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using OverWatchELD.VtcBot.Commands;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;
using System.Text.Json;

namespace OverWatchELD.VtcBot;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task Main()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        var services = new BotServices
        {
            ThreadStore = new ThreadMapStore(Path.Combine(dataDir, "thread_map.json")),
            DispatchStore = new DispatchSettingsStore(Path.Combine(dataDir, "dispatch_settings.json"), JsonReadOpts, JsonWriteOpts),
            RosterStore = new VtcRosterStore(Path.Combine(dataDir, "vtc_roster.json"), JsonReadOpts, JsonWriteOpts),
            LinkCodeStore = new LinkCodeStore(Path.Combine(dataDir, "link_codes.json"), JsonReadOpts, JsonWriteOpts),
            LinkedDriversStore = new LinkedDriversStore(Path.Combine(dataDir, "linked_drivers.json"), JsonReadOpts, JsonWriteOpts),
            PerformanceStore = new PerformanceStore(Path.Combine(dataDir, "performance"), JsonReadOpts, JsonWriteOpts)
        };

        var token = (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN env var.");
            return;
        }

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent |
                GatewayIntents.GuildMembers |
                GatewayIntents.GuildPresences
        });

        services.Client = _client;

        _client.MessageReceived += msg => BotCommandHandler.HandleMessageAsync(msg, services);

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        // move your huge RegisterRoutes body into ApiRoutes.Register(app, services)
        // ApiRoutes.Register(app, services);

        await app.RunAsync();
    }
}
