// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ Discord bot works: !ping, !link CODE
// ✅ Bot serves VTC endpoints (prevents 404 "page not found"):
//    /api/vtc/servers
//    /api/vtc/name?guildId=...
//    /api/vtc/roster?guildId=...
//    /api/vtc/announcements?guildId=...
//    /api/vtc/pair/claim?code=...
// ✅ Optional: still proxies /api/messages/* to HUB (if you use it)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Optional Hub proxy for /api/messages
    private const string DefaultHubBase = "https://overwatcheld-saas-production.up.railway.app";
    private static readonly HttpClient HubHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    // Pairing codes captured from Discord `!link CODE`
    private sealed record PendingPair(string GuildId, string VtcName, DateTimeOffset CreatedUtc);

    private static readonly ConcurrentDictionary<string, PendingPair> PendingPairs = new(StringComparer.OrdinalIgnoreCase);

    public static async Task Main(string[] args)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN (or BOT_TOKEN).");
            return;
        }

        // -----------------------------
        // Discord client
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
                // If you want full roster from Discord members, enable this in dev portal + add:
                // | GatewayIntents.GuildMembers
        };

        _client = new DiscordSocketClient(socketConfig);
        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += () =>
        {
            // ✅ Public release safe: no usernames printed
            Console.WriteLine("✅ READY");
            return Task.CompletedTask;
        };
        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // -----------------------------
        // HTTP API (Railway)
        // -----------------------------
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        // Hub proxy base (only used for /api/messages if you want)
        var hubBase = (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? DefaultHubBase).Trim();
        hubBase = NormalizeAbsoluteBaseUrl(hubBase);
        HubHttp.BaseAddress = new Uri(hubBase + "/", UriKind.Absolute);

        var app = builder.Build();

        app.MapGet("/", () => Results.Ok(new { ok = true, service = "OverWatchELD.VtcBot" }));
        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/build", () => Results.Ok(new { ok = true, build = "VtcBot-2026-02-28-public" }));

        // -----------------------------
        // VTC endpoints (serve locally on the BOT)
        // -----------------------------

        app.MapGet("/api/vtc/servers", () =>
        {
            var guilds = (_client?.Guilds ?? Array.Empty<SocketGuild>())
                .Select(g => new { id = g.Id.ToString(), name = g.Name ?? "" })
                .OrderBy(x => x.name)
                .ThenBy(x => x.id)
                .ToList();

            return Results.Ok(new { ok = true, servers = guilds });
        });

        app.MapGet("/api/vtc/name", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { error = "GuildNotFound" });

            // ✅ Public release safe: returns server (guild) name, not user name
            return Results.Ok(new { ok = true, guildId = guildId, vtcName = g.Name ?? "" });
        });

        app.MapGet("/api/vtc/roster", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { error = "GuildNotFound" });

            // NOTE: If you do NOT enable GuildMembers intent, this can be partial.
            // Still useful for basic display.
            var users = g.Users
                .Where(u => !u.IsBot)
                .Select(u => new
                {
                    id = u.Id.ToString(),
                    name = (u.Nickname ?? u.Username) // ✅ safe: this is a member display name, not YOUR personal name specifically
                })
                .OrderBy(x => x.name)
                .Take(200)
                .ToList();

            await Task.CompletedTask;
            return Results.Ok(new { ok = true, guildId, drivers = users });
        });

        app.MapGet("/api/vtc/announcements", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { error = "GuildNotFound" });

            // ✅ Configure announcements channel id via env:
            // ANNOUNCEMENTS_CHANNEL_ID_<GUILDID> = 123...
            // or ANNOUNCEMENTS_CHANNEL_ID = 123...
            var key1 = "ANNOUNCEMENTS_CHANNEL_ID_" + guildId;
            var chanStr = (Environment.GetEnvironmentVariable(key1) ??
                           Environment.GetEnvironmentVariable("ANNOUNCEMENTS_CHANNEL_ID") ??
                           "").Trim();

            if (!ulong.TryParse(chanStr, out var chanId))
            {
                // Safe default: no announcements configured
                return Results.Ok(new { ok = true, guildId, items = Array.Empty<object>() });
            }

            var chan = g.GetTextChannel(chanId);
            if (chan == null)
                return Results.Ok(new { ok = true, guildId, items = Array.Empty<object>() });

            // Pull last 25 messages (requires Read Message History permission)
            var msgs = await chan.GetMessagesAsync(25).FlattenAsync();

            var items = msgs
                .OrderBy(m => m.Timestamp)
                .Select(m => new
                {
                    text = m.Content ?? "",
                    author = m.Author?.Username ?? "Discord",
                    createdUtc = m.Timestamp.UtcDateTime.ToString("O")
                })
                .ToList();

            return Results.Ok(new { ok = true, guildId, items });
        });

        // Pair claim: ELD calls this after user types !link CODE in Discord
        app.MapGet("/api/vtc/pair/claim", (HttpRequest req) =>
        {
            var code = (req.Query["code"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                return Results.Json(new { ok = false, error = "MissingCode", message = "Provide ?code=XXXXXX" }, statusCode: 400);

            CleanupExpiredPairs();

            if (!PendingPairs.TryRemove(code, out var p))
                return Results.Json(new { ok = false, error = "InvalidOrExpiredCode" }, statusCode: 404);

            return Results.Ok(new { ok = true, guildId = p.GuildId, vtcName = p.VtcName });
        });

        // -----------------------------
        // Optional: proxy /api/messages/* to HUB (keep your existing data store)
        // -----------------------------
        async Task<IResult> ProxyToHub(HttpRequest req)
        {
            var path = req.Path.HasValue ? req.Path.Value! : "/";
            var qs = req.QueryString.HasValue ? req.QueryString.Value! : "";
            var rel = path.TrimStart('/') + qs;

            HttpResponseMessage resp;

            if (HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method) || HttpMethods.IsPatch(req.Method))
            {
                using var reader = new StreamReader(req.Body);
                var body = await reader.ReadToEndAsync();
                using var content = new StringContent(body, Encoding.UTF8, req.ContentType ?? "application/json");
                resp = await HubHttp.SendAsync(new HttpRequestMessage(new HttpMethod(req.Method), rel) { Content = content });
            }
            else
            {
                resp = await HubHttp.SendAsync(new HttpRequestMessage(new HttpMethod(req.Method), rel));
            }

            var txt = await resp.Content.ReadAsStringAsync();
            return Results.Content(txt, resp.Content.Headers.ContentType?.ToString() ?? "application/json", Encoding.UTF8, (int)resp.StatusCode);
        }

        app.MapMethods("/api/messages", new[] { "GET", "POST" }, (HttpRequest req) => ProxyToHub(req));
        app.MapMethods("/api/messages/{**rest}", new[] { "GET", "POST" }, (HttpRequest req) => ProxyToHub(req));

        // Start HTTP server
        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        Console.WriteLine($"🔁 Hub proxy base: {hubBase}");

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

        if (body.StartsWith("link", StringComparison.OrdinalIgnoreCase))
        {
            var code = body.Length > 4 ? body.Substring(4).Trim() : "";
            if (string.IsNullOrWhiteSpace(code))
            {
                await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`");
                return;
            }

            // Determine guild
            var guildId = "";
            var vtcName = "";

            if (msg.Channel is SocketGuildChannel gc)
            {
                guildId = gc.Guild.Id.ToString();
                vtcName = gc.Guild.Name ?? "";
            }

            if (string.IsNullOrWhiteSpace(guildId))
            {
                await msg.Channel.SendMessageAsync("❌ This command must be used in a server channel (not DM).");
                return;
            }

            CleanupExpiredPairs();
            PendingPairs[code] = new PendingPair(guildId, vtcName, DateTimeOffset.UtcNow);

            await msg.Channel.SendMessageAsync("✅ Link code received. Paste it into the ELD to complete pairing.");
            return;
        }
    }

    private static void CleanupExpiredPairs()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var kv in PendingPairs)
        {
            if (kv.Value.CreatedUtc < cutoff)
                PendingPairs.TryRemove(kv.Key, out _);
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
