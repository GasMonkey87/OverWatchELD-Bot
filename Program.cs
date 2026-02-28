using System;
using System.IO;
using System.Net.Http;
using System.Text;
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

// Hub base (for /api/messages proxy)
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

// Optional server display name (public-safe, set by server owner)
var vtcNameEnv = (Environment.GetEnvironmentVariable("VTC_NAME") ?? "").Trim();

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

// Discord state so we can return server name + guild list reliably
builder.Services.AddSingleton<DiscordState>();
builder.Services.AddHostedService<DiscordBotHostedService>();

var app = builder.Build();

// ---------------------------
// ROUTES
// ---------------------------
app.MapGet("/health", () => Results.Json(new { ok = true }));

// ✅ VTC identity endpoints used by ELD UI
// Public-safe: never returns a person's Discord name
app.MapGet("/api/vtc/name", (HttpRequest req, DiscordState st) =>
{
    var gid = ResolveGuildId(req) ?? st.DefaultGuildId;

    // Prefer: Discord guild name (server name)
    var serverName = st.ResolveGuildName(gid);

    // Else: env VTC_NAME (server owner configured)
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = vtcNameEnv;

    // Else: generic safe default
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = "OverWatch ELD";

    return Results.Json(new { ok = true, guildId = gid, name = serverName });
});

app.MapGet("/api/vtc/status", (HttpRequest req, DiscordState st) =>
{
    var gid = ResolveGuildId(req) ?? st.DefaultGuildId;

    var serverName = st.ResolveGuildName(gid);
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = vtcNameEnv;
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = "OverWatch ELD";

    var connected = !string.IsNullOrWhiteSpace(gid);
    var ready = connected && st.DiscordReady;

    return Results.Json(new
    {
        ok = true,
        connected,
        ready,
        guildId = gid,
        serverName
    });
});

// Aliases some builds call
app.MapGet("/api/vtc", (HttpRequest req, DiscordState st) =>
{
    var gid = ResolveGuildId(req) ?? st.DefaultGuildId;

    var serverName = st.ResolveGuildName(gid);
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = vtcNameEnv;
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = "OverWatch ELD";

    return Results.Json(new { ok = true, enabled = true, guildId = gid, name = serverName });
});

// Guild list endpoints (for dropdown; still public-safe: server names only)
app.MapGet("/api/discord/guilds", (DiscordState st) => Results.Json(new { ok = true, items = st.GetGuildItems() }));
app.MapGet("/api/guilds", (DiscordState st) => Results.Json(new { ok = true, items = st.GetGuildItems() }));
app.MapGet("/api/servers", (DiscordState st) => Results.Json(new { ok = true, items = st.GetGuildItems() }));
app.MapGet("/api/discord/servers", (DiscordState st) => Results.Json(new { ok = true, items = st.GetGuildItems() }));

// ✅ Messages proxy to Hub (your working flow)
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

// ✅ Public-safe fallback: prevents ELD from failing due to missing endpoints
app.MapMethods("/api/{*path}", new[] { "GET", "POST", "PUT", "DELETE" }, (HttpRequest req, DiscordState st) =>
{
    var gid = ResolveGuildId(req) ?? st.DefaultGuildId;

    var serverName = st.ResolveGuildName(gid);
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = vtcNameEnv;
    if (string.IsNullOrWhiteSpace(serverName))
        serverName = "OverWatch ELD";

    return Results.Json(new
    {
        ok = true,
        note = "Fallback endpoint (unknown route).",
        guildId = gid,
        serverName
    });
});

Console.WriteLine($"🌐 Bot API listening on 0.0.0.0:{port}");
Console.WriteLine($"🔁 HUB_BASE_URL = {hubBase}");
Console.WriteLine($"🏷 DEFAULT_GUILD_ID = {Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "(null)"}");
Console.WriteLine($"🏷 VTC_NAME = {(string.IsNullOrWhiteSpace(vtcNameEnv) ? "(not set)" : "(set)")}");
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

    public bool DiscordReady { get; set; }

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

    public string? ResolveGuildName(string? guildId)
    {
        if (string.IsNullOrWhiteSpace(guildId)) return null;
        var gid = guildId.Trim();

        lock (_lock)
        {
            foreach (var g in _guilds)
                if (string.Equals(g.Id, gid, StringComparison.OrdinalIgnoreCase))
                    return g.Name;
        }

        return null;
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
            Console.WriteLine("⚠️ No Discord token. HTTP API only.");
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

        _client.Ready += () =>
        {
            _state.DiscordReady = true;

            try
            {
                var guilds = _client.Guilds;
                var items = new DiscordState.GuildItem[guilds.Count];
                var i = 0;

                foreach (var g in guilds)
                    items[i++] = new DiscordState.GuildItem { Id = g.Id.ToString(), Name = g.Name };

                _state.SetGuilds(items);

                if (string.IsNullOrWhiteSpace(_state.DefaultGuildId) && items.Length > 0)
                    _state.DefaultGuildId = items[0].Id;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Guild cache error: " + ex.Message);
            }

            Console.WriteLine("✅ Discord READY");
            return Task.CompletedTask;
        };

        // Public-safe commands (no usernames echoed)
        _client.MessageReceived += async raw =>
        {
            if (raw is not SocketUserMessage msg) return;
            if (msg.Author.IsBot) return;

            var content = (msg.Content ?? "").Trim();
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

                // ✅ Public-safe response
                await msg.Channel.SendMessageAsync("✅ Link code received. Open your ELD app to confirm it linked.");
                return;
            }

            await msg.Channel.SendMessageAsync("Commands: `!ping`, `!link 123456`");
        };

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
