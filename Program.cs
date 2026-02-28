using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------
// ENVIRONMENT CONFIG
// ---------------------------

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

var hubBaseRaw = Environment.GetEnvironmentVariable("HUB_BASE_URL");
var hubBase = string.IsNullOrWhiteSpace(hubBaseRaw)
    ? "https://overwatcheld-saas-production.up.railway.app"
    : hubBaseRaw.Trim();

if (!hubBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
    !hubBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
{
    hubBase = "https://" + hubBase;
}

hubBase = hubBase.TrimEnd('/');

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

// Discord + Guild cache (for dropdown)
builder.Services.AddSingleton<DiscordState>();
builder.Services.AddHostedService<DiscordBotHostedService>();

var app = builder.Build();

// ---------------------------
// ROUTES
// ---------------------------

app.MapGet("/health", () => Results.Json(new { ok = true }));

// ✅ ELD-friendly endpoints (prevent 404 no matter what it calls)
app.MapGet("/api/config", (HttpRequest req, DiscordState state) =>
{
    var gid = ResolveGuildId(req);
    return Results.Json(new
    {
        ok = true,
        guildId = gid,
        defaultGuildId = state.DefaultGuildId,
        hubBaseUrl = hubBase
    });
});

app.MapGet("/api/discord/default", (HttpRequest req, DiscordState state) =>
{
    var gid = ResolveGuildId(req) ?? state.DefaultGuildId;
    if (string.IsNullOrWhiteSpace(gid))
        return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

    return Results.Json(new { ok = true, guildId = gid });
});

// These four are common names for the “Discord Server” dropdown
app.MapGet("/api/discord/guilds", (DiscordState state) => Results.Json(new { ok = true, items = state.GetGuildItems() }));
app.MapGet("/api/guilds", (DiscordState state) => Results.Json(new { ok = true, items = state.GetGuildItems() }));
app.MapGet("/api/servers", (DiscordState state) => Results.Json(new { ok = true, items = state.GetGuildItems() }));
app.MapGet("/api/discord/servers", (DiscordState state) => Results.Json(new { ok = true, items = state.GetGuildItems() }));

// ✅ Proxy messages (already working)
app.MapGet("/api/messages", async (HttpRequest req, HttpContext ctx, HttpClient http) =>
{
    var guildId = ResolveGuildId(req);
    if (string.IsNullOrWhiteSpace(guildId))
    {
        return Results.Json(new
        {
            error = "MissingGuildId",
            traceId = ctx.TraceIdentifier,
            hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
        }, statusCode: 400);
    }

    try
    {
        var target = BuildTargetUrl($"{hubBase}/api/messages", req.QueryString.Value, guildId!);
        using var resp = await http.GetAsync(target);
        var body = await resp.Content.ReadAsStringAsync();

        return Results.Content(body,
            resp.Content.Headers.ContentType?.ToString() ?? "application/json",
            Encoding.UTF8,
            (int)resp.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "ProxyFailed", traceId = ctx.TraceIdentifier, message = ex.Message }, statusCode: 502);
    }
});

app.MapPost("/api/messages/send", async (HttpRequest req, HttpContext ctx, HttpClient http) =>
{
    var guildId = ResolveGuildId(req);
    if (string.IsNullOrWhiteSpace(guildId))
    {
        return Results.Json(new
        {
            error = "MissingGuildId",
            traceId = ctx.TraceIdentifier,
            hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
        }, statusCode: 400);
    }

    try
    {
        var target = BuildTargetUrl($"{hubBase}/api/messages/send", req.QueryString.Value, guildId!);

        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(target, content);
        var body = await resp.Content.ReadAsStringAsync();

        return Results.Content(body,
            resp.Content.Headers.ContentType?.ToString() ?? "application/json",
            Encoding.UTF8,
            (int)resp.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "ProxyFailed", traceId = ctx.TraceIdentifier, message = ex.Message }, statusCode: 502);
    }
});

Console.WriteLine($"🌐 Bot API listening on 0.0.0.0:{port}");
Console.WriteLine($"🔁 HUB_BASE_URL = {hubBase}");
Console.WriteLine($"🏷 DEFAULT_GUILD_ID = {Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "(null)"}");

app.Run();

// ---------------------------
// HELPERS
// ---------------------------

static string? ResolveGuildId(HttpRequest req)
{
    var q = req.Query["guildId"].ToString();
    if (!string.IsNullOrWhiteSpace(q)) return q.Trim();

    if (req.Headers.TryGetValue("X-Guild-Id", out var h))
    {
        var hv = h.ToString();
        if (!string.IsNullOrWhiteSpace(hv)) return hv.Trim();
    }

    var env = Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

    return null;
}

static string BuildTargetUrl(string basePath, string? qs, string guildId)
{
    basePath = basePath.TrimEnd('/');

    if (string.IsNullOrWhiteSpace(qs))
        return $"{basePath}?guildId={Uri.EscapeDataString(guildId)}";

    if (!qs.StartsWith("?"))
        qs = "?" + qs;

    if (qs.Contains("guildId=", StringComparison.OrdinalIgnoreCase))
        return $"{basePath}{qs}";

    return $"{basePath}{qs}&guildId={Uri.EscapeDataString(guildId)}";
}

// ---------------------------
// DISCORD STATE + HOSTED SERVICE
// ---------------------------

sealed class DiscordState
{
    private readonly object _lock = new();
    private GuildItem[] _guilds = Array.Empty<GuildItem>();

    public string? DefaultGuildId { get; set; } =
        (Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "").Trim();

    public void SetGuilds(GuildItem[] items)
    {
        lock (_lock) { _guilds = items; }
    }

    public GuildItem[] GetGuildItems()
    {
        lock (_lock) { return _guilds; }
    }

    public sealed class GuildItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}

sealed class DiscordBotHostedService : BackgroundService
{
    private readonly DiscordState _state;
    private DiscordSocketClient? _client;

    public DiscordBotHostedService(DiscordState state) => _state = state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("⚠️ No DISCORD_TOKEN/BOT_TOKEN set. HTTP API only.");
            return;
        }

        var config = new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages |
                GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(config);

        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };

        _client.Ready += async () =>
        {
            Console.WriteLine($"✅ Discord READY as {_client.CurrentUser}");

            try
            {
                // Cache guilds for the ELD dropdown
                var guilds = _client.Guilds;
                var items = new DiscordState.GuildItem[guilds.Count];
                var i = 0;
                foreach (var g in guilds)
                    items[i++] = new DiscordState.GuildItem { Id = g.Id.ToString(), Name = g.Name };

                _state.SetGuilds(items);

                // If no default guild id set, pick the first one
                if (string.IsNullOrWhiteSpace(_state.DefaultGuildId) && items.Length > 0)
                    _state.DefaultGuildId = items[0].Id;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Guild cache error: " + ex.Message);
            }

            await Task.CompletedTask;
        };

        _client.MessageReceived += HandleDiscordMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Keep alive
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch { }
        try { await _client.StopAsync(); } catch { }
        try { await _client.LogoutAsync(); } catch { }
    }

    private static async Task HandleDiscordMessageAsync(SocketMessage raw)
    {
        if (raw is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        var content = (msg.Content ?? "").Trim();

        // We only care about prefix commands here
        if (!content.StartsWith("!")) return;

        var body = content.Substring(1).Trim();

        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (body.StartsWith("link", StringComparison.OrdinalIgnoreCase))
        {
            var code = body.Length > 4 ? body.Substring(4).Trim() : "";
            if (string.IsNullOrWhiteSpace(code))
            {
                await msg.Channel.SendMessageAsync("Usage: `!link 123456`");
                return;
            }

            await msg.Channel.SendMessageAsync($"✅ Link received for **{msg.Author.Username}**: `{code}`");
            return;
        }

        await msg.Channel.SendMessageAsync("Commands: `!ping`, `!link 123456`");
    }
}
