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

    // Fallback if HUB_BASE_URL not set
    private const string DefaultHubBase = "https://overwatcheld-saas-production.up.railway.app";

    private static readonly HttpClient HubHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static async Task Main(string[] args)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN (or BOT_TOKEN). ");
            return;
        }

        // -----------------------------
        // HTTP API (Railway)
        // -----------------------------
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        // Resolve hub base URL
        var hubBase = (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? DefaultHubBase).Trim();
        hubBase = NormalizeAbsoluteBaseUrl(hubBase);

        // ✅ Fix ProxyFailed / invalid request URI:
        // Always set BaseAddress and proxy with RELATIVE paths.
        HubHttp.BaseAddress = new Uri(hubBase + "/", UriKind.Absolute);

        var defaultGuildId = (Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "").Trim();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        // -----------------------------
        // Proxy helpers
        // -----------------------------
        static string WithDefaultGuildId(string pathAndQuery, string defaultGuildId)
        {
            if (string.IsNullOrWhiteSpace(defaultGuildId)) return pathAndQuery;

            // already has guildId?
            if (pathAndQuery.Contains("guildId=", StringComparison.OrdinalIgnoreCase))
                return pathAndQuery;

            // add guildId
            return pathAndQuery.Contains("?")
                ? pathAndQuery + "&guildId=" + Uri.EscapeDataString(defaultGuildId)
                : pathAndQuery + "?guildId=" + Uri.EscapeDataString(defaultGuildId);
        }

        async Task<IResult> ProxyGet(HttpRequest req)
        {
            var path = req.Path.HasValue ? req.Path.Value! : "/";
            var qs = req.QueryString.HasValue ? req.QueryString.Value! : "";
            var rel = path.TrimStart('/') + qs;

            // inject default guildId for endpoints that need it
            if (path.StartsWith("/api/messages", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/vtc", StringComparison.OrdinalIgnoreCase))
            {
                rel = WithDefaultGuildId(rel, defaultGuildId);
            }

            using var resp = await HubHttp.GetAsync(rel);
            var body = await resp.Content.ReadAsStringAsync();

            return Results.Content(
                body,
                resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8,
                (int)resp.StatusCode);
        }

        async Task<IResult> ProxyPost(HttpRequest req)
        {
            var path = req.Path.HasValue ? req.Path.Value! : "/";
            var qs = req.QueryString.HasValue ? req.QueryString.Value! : "";
            var rel = path.TrimStart('/') + qs;

            if (path.StartsWith("/api/messages", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/vtc", StringComparison.OrdinalIgnoreCase))
            {
                rel = WithDefaultGuildId(rel, defaultGuildId);
            }

            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();

            using var content = new StringContent(json, Encoding.UTF8, req.ContentType ?? "application/json");
            using var resp = await HubHttp.PostAsync(rel, content);
            var body = await resp.Content.ReadAsStringAsync();

            return Results.Content(
                body,
                resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8,
                (int)resp.StatusCode);
        }

        // -----------------------------
        // Proxied endpoints (ELD expects these)
        // -----------------------------
        app.MapGet("/api/messages", ProxyGet);
        app.MapPost("/api/messages/send", ProxyPost);

        // ✅ Proxy all VTC endpoints (name, roster, servers, announcements, pair/claim, etc.)
        app.MapGet("/api/vtc/{**rest}", ProxyGet);
        app.MapPost("/api/vtc/{**rest}", ProxyPost);

        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        Console.WriteLine($"🔁 Proxying to hub: {hubBase}");
        if (!string.IsNullOrWhiteSpace(defaultGuildId))
            Console.WriteLine($"🏷️ DEFAULT_GUILD_ID set: {defaultGuildId}");

        // -----------------------------
        // Discord bot
        // -----------------------------
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
            // ✅ Public release safety: do not print personal Discord names here.
            Console.WriteLine("✅ READY");
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        Console.WriteLine("🤖 Discord bot started.");
        await Task.Delay(-1);
    }

    private static async Task HandleMessageAsync(SocketMessage raw)
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

        // Keep !link (the hub will handle the pairing flow via /api/vtc/pair/*).
        // This bot message is just a user-friendly acknowledgement.
        if (body.StartsWith("link", StringComparison.OrdinalIgnoreCase))
        {
            var code = body.Length > 4 ? body.Substring(4).Trim() : "";
            if (string.IsNullOrWhiteSpace(code))
            {
                await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`\nThen paste that code into the ELD VTC Pair box.");
                return;
            }

            await msg.Channel.SendMessageAsync("✅ Link code received. Paste it into the ELD to complete pairing.");
            return;
        }
    }

    private static string NormalizeAbsoluteBaseUrl(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) url = DefaultHubBase;

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        while (url.EndsWith("/")) url = url[..^1];

        _ = new Uri(url, UriKind.Absolute);
        return url;
    }
}
