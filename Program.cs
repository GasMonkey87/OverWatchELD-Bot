// Program.cs ✅ FULL COPY/REPLACE (BOT SERVICE)
// - HTTP API for ELD + proxy to Hub
// - Fixes MissingGuildId using query/header/DEFAULT_GUILD_ID
// - Discord bot included (no HasCharPrefix extension usage)
// - Railway friendly: binds to PORT

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    private static DiscordSocketClient? _client;

    // You can set HUB_BASE_URL in Railway to override
    // Example: https://overwatcheld-saas-production.up.railway.app
    private static readonly string HubBase =
        (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? "https://overwatcheld-saas-production.up.railway.app")
        .Trim()
        .TrimEnd('/');

    private static readonly HttpClient ProxyHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task Main(string[] args)
    {
        // -----------------------------
        // HTTP API (Railway)
        // -----------------------------
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        app.MapGet("/health", () => Results.Json(new { ok = true }));

        // GET /api/messages  -> proxy to HUB /api/messages (adds guildId if needed)
        app.MapGet("/api/messages", async (HttpRequest req, HttpContext ctx) =>
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

            // Preserve any caller query params; ensure guildId exists
            var qs = req.QueryString.HasValue ? req.QueryString.Value! : "";
            var target = BuildTargetUrl($"{HubBase}/api/messages", qs, guildId!);

            using var resp = await ProxyHttp.GetAsync(target);
            var body = await resp.Content.ReadAsStringAsync();

            return Results.Content(
                body,
                resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8,
                (int)resp.StatusCode
            );
        });

        // POST /api/messages/send -> proxy to HUB /api/messages/send (adds guildId if needed)
        app.MapPost("/api/messages/send", async (HttpRequest req, HttpContext ctx) =>
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

            var qs = req.QueryString.HasValue ? req.QueryString.Value! : "";
            var target = BuildTargetUrl($"{HubBase}/api/messages/send", qs, guildId!);

            // Read raw JSON body and forward
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await ProxyHttp.PostAsync(target, content);
            var body = await resp.Content.ReadAsStringAsync();

            return Results.Content(
                body,
                resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8,
                (int)resp.StatusCode
            );
        });

        // Start web server in background so Discord bot can run too
        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        Console.WriteLine($"🔁 Proxy HUB_BASE_URL = {HubBase}");

        // -----------------------------
        // Discord bot
        // -----------------------------
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("⚠️ DISCORD_TOKEN/BOT_TOKEN not set. HTTP API will still run.");
            await Task.Delay(-1);
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
        await Task.Delay(-1);
    }

    // -------- Helpers --------

    private static string? ResolveGuildId(HttpRequest req)
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

        // 3) env DEFAULT_GUILD_ID
        var env = Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        return null;
    }

    private static string BuildTargetUrl(string basePath, string qs, string guildId)
    {
        // Ensure guildId is included even if client didn't send it
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

            // If you already have linking storage logic elsewhere, call it here.
            await msg.Channel.SendMessageAsync($"✅ Link received for **{msg.Author.Username}**: `{code}`");
            return;
        }

        await msg.Channel.SendMessageAsync("Commands: `!ping`, `!link CODE`");
    }
}
