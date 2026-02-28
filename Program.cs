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

// -----------------------------
// Config / env
// -----------------------------
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var hubBase =
    (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? "https://overwatcheld-saas-production.up.railway.app")
    .Trim()
    .TrimEnd('/');

// Bind to Railway PORT
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Shared HttpClient for proxying
builder.Services.AddSingleton(new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
});

// Discord bot runs as hosted service (safe, won’t break web server)
builder.Services.AddHostedService(sp => new DiscordBotHostedService());

var app = builder.Build();

// -----------------------------
// Routes
// -----------------------------
app.MapGet("/health", () => Results.Json(new { ok = true }));

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

        return Results.Content(
            body,
            resp.Content.Headers.ContentType?.ToString() ?? "application/json",
            Encoding.UTF8,
            (int)resp.StatusCode
        );
    }
    catch (Exception ex)
    {
        // Never crash the endpoint—return a clean error
        return Results.Json(new
        {
            error = "ProxyFailed",
            traceId = ctx.TraceIdentifier,
            message = ex.Message
        }, statusCode: 502);
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

        return Results.Content(
            body,
            resp.Content.Headers.ContentType?.ToString() ?? "application/json",
            Encoding.UTF8,
            (int)resp.StatusCode
        );
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            error = "ProxyFailed",
            traceId = ctx.TraceIdentifier,
            message = ex.Message
        }, statusCode: 502);
    }
});

Console.WriteLine($"🌐 Bot API listening on 0.0.0.0:{port}");
Console.WriteLine($"🔁 HUB_BASE_URL = {hubBase}");
Console.WriteLine($"🏷 DEFAULT_GUILD_ID = {Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "(null)"}");

app.Run();

static string? ResolveGuildId(HttpRequest req)
{
    // 1) query ?guildId=
    var q = req.Query["guildId"].ToString();
    if (!string.IsNullOrWhiteSpace(q)) return q.Trim();

    // 2) header X-Guild-Id
    if (req.Headers.TryGetValue("X-Guild-Id", out var h))
    {
        var hv = h.ToString();
        if (!string.IsNullOrWhiteSpace(hv)) return hv.Trim();
    }

    // 3) env DEFAULT_GUILD_ID (Railway)
    var env = Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

    return null;
}

static string BuildTargetUrl(string basePath, string? qs, string guildId)
{
    qs = qs ?? "";

    if (string.IsNullOrWhiteSpace(qs))
        return $"{basePath}?guildId={Uri.EscapeDataString(guildId)}";

    // If caller already provided guildId, keep it
    if (qs.IndexOf("guildId=", StringComparison.OrdinalIgnoreCase) >= 0)
        return $"{basePath}{qs}";

    // Append guildId to existing query string
    if (qs.StartsWith("?"))
        return $"{basePath}{qs}&guildId={Uri.EscapeDataString(guildId)}";

    return $"{basePath}?{qs}&guildId={Uri.EscapeDataString(guildId)}";
}

// -----------------------------
// Discord hosted service
// -----------------------------
sealed class DiscordBotHostedService : BackgroundService
{
    private DiscordSocketClient? _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("⚠️ DISCORD_TOKEN/BOT_TOKEN not set. Running HTTP API only.");
            return;
        }

        var socketConfig = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 50,
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages |
                GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(socketConfig);
        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += () =>
        {
            Console.WriteLine($"✅ Discord READY as {_client.CurrentUser} (id: {_client.CurrentUser.Id})");
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleDiscordMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        Console.WriteLine("🤖 Discord bot started.");

        // Keep alive until shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch { }

        try { await _client.StopAsync(); } catch { }
        try { await _client.LogoutAsync(); } catch { }
    }

    private static async Task HandleDiscordMessageAsync(SocketMessage raw)
    {
        if (raw is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        var body = content.Substring(1).Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

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
                await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`");
                return;
            }

            await msg.Channel.SendMessageAsync($"✅ Link received for **{msg.Author.Username}**: `{code}`");
            return;
        }

        await msg.Channel.SendMessageAsync("Commands: `!ping`, `!link CODE`");
    }
}
