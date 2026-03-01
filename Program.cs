// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ Discord bot: !ping, !link CODE
// ✅ VTC endpoints:
//    /api/vtc/servers
//    /api/vtc/name?guildId=...
//    /api/vtc/roster?guildId=...         (includes truck/location if heartbeat posted)
//    /api/vtc/announcements?guildId=...
//    /api/vtc/pair/claim?code=...        (returns discordUserId + discordUsername)
//    POST /api/vtc/roster/heartbeat
//
// ✅ NEW: Dispatch messaging bridge (two-way)
//    POST /api/dispatch/send             (ELD -> Discord #dispatch)
//    GET  /api/dispatch/inbox?guildId=   (ELD polls Discord #dispatch -> ELD)
//    - Discord->ELD sender is "dispatch" (always)
//    - Optional webhook posting as username "dispatch"
//
// ✅ Optional: proxies /api/messages/* to HUB (unchanged)

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    // ✅ Pairing codes captured from Discord `!link CODE` (includes Discord user identity)
    private sealed record PendingPair(
        string GuildId,
        string VtcName,
        string DiscordUserId,
        string DiscordUsername,
        DateTimeOffset CreatedUtc
    );

    private static readonly ConcurrentDictionary<string, PendingPair> PendingPairs = new(StringComparer.OrdinalIgnoreCase);

    // ✅ Roster live state (ELD -> Bot telemetry heartbeat)
    private sealed class DriverLiveState
    {
        public string GuildId { get; set; } = "";
        public string DiscordUserId { get; init; } = "";
        public string DiscordUsername { get; set; } = "";

        public string Truck { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string DutyStatus { get; set; } = "";

        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    // Key: guildId:userId
    private static readonly ConcurrentDictionary<string, DriverLiveState> Live = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Heartbeat payload ELD will POST
    private sealed class RosterHeartbeat
    {
        public string? GuildId { get; set; }
        public string? DiscordUserId { get; set; }
        public string? DiscordUsername { get; set; }

        public string? Truck { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? DutyStatus { get; set; }
    }

    // -----------------------------
    // Dispatch bridge DTOs
    // -----------------------------
    private sealed class DispatchSendRequest
    {
        public string? GuildId { get; set; }    // required
        public string? Text { get; set; }       // required
        public string? From { get; set; }       // optional (e.g. driver name); Discord will still show "dispatch" if webhook used
    }

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
                GatewayIntents.GuildMembers |   // ✅ required for full roster (also enable in Dev Portal)
                GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages |
                GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(socketConfig);
        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += () =>
        {
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
        app.MapGet("/build", () => Results.Ok(new { ok = true, build = "VtcBot-2026-02-28-public+pairing+rosterLive+dispatchBridge" }));

        // -----------------------------
        // VTC endpoints
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

            return Results.Ok(new { ok = true, guildId = guildId, vtcName = g.Name ?? "" });
        });

        app.MapPost("/api/vtc/roster/heartbeat", async (HttpRequest req) =>
        {
            try
            {
                using var reader = new StreamReader(req.Body);
                var body = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(body))
                    return Results.Json(new { ok = false, error = "EmptyBody" }, statusCode: 400);

                var hb = JsonSerializer.Deserialize<RosterHeartbeat>(body, JsonReadOpts);
                if (hb == null)
                    return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

                var guildId = (hb.GuildId ?? "").Trim();
                var userId = (hb.DiscordUserId ?? "").Trim();

                if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(userId))
                    return Results.Json(new { ok = false, error = "MissingGuildIdOrUserId" }, statusCode: 400);

                var key = $"{guildId}:{userId}";

                Live.AddOrUpdate(key,
                    _ => new DriverLiveState
                    {
                        GuildId = guildId,
                        DiscordUserId = userId,
                        DiscordUsername = (hb.DiscordUsername ?? "").Trim(),
                        Truck = (hb.Truck ?? "").Trim(),
                        City = (hb.City ?? "").Trim(),
                        State = (hb.State ?? "").Trim(),
                        DutyStatus = (hb.DutyStatus ?? "").Trim(),
                        LastSeenUtc = DateTimeOffset.UtcNow
                    },
                    (_, existing) =>
                    {
                        existing.GuildId = guildId;

                        var dn = (hb.DiscordUsername ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(dn)) existing.DiscordUsername = dn;

                        var tr = (hb.Truck ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(tr)) existing.Truck = tr;

                        var c = (hb.City ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(c)) existing.City = c;

                        var s = (hb.State ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(s)) existing.State = s;

                        var ds = (hb.DutyStatus ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(ds)) existing.DutyStatus = ds;

                        existing.LastSeenUtc = DateTimeOffset.UtcNow;
                        return existing;
                    });

                CleanupOldLiveStates();
                return Results.Ok(new { ok = true });
            }
            catch
            {
                return Results.Json(new { ok = false, error = "ServerError" }, statusCode: 500);
            }
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

            // Force member download for reliable roster
            try { await g.DownloadUsersAsync(); } catch { }

            CleanupOldLiveStates();

            var users = g.Users
                .Where(u => !u.IsBot)
                .Select(u =>
                {
                    var uid = u.Id.ToString();
                    var username = u.Username ?? "";
                    var display = (u.Nickname ?? u.Username) ?? "";

                    var key = $"{guildId}:{uid}";
                    Live.TryGetValue(key, out var live);

                    var truck = live?.Truck ?? "";
                    var city = live?.City ?? "";
                    var state = live?.State ?? "";
                    var duty = live?.DutyStatus ?? "";
                    var lastSeenUtc = live?.LastSeenUtc ?? DateTimeOffset.MinValue;

                    var hbName = live?.DiscordUsername ?? "";
                    if (!string.IsNullOrWhiteSpace(hbName))
                        username = hbName;

                    return new
                    {
                        discordUserId = uid,
                        discordUsername = username,
                        displayName = display,
                        truck = truck,
                        city = city,
                        state = state,
                        dutyStatus = duty,
                        lastSeenUtc = lastSeenUtc == DateTimeOffset.MinValue ? "" : lastSeenUtc.UtcDateTime.ToString("O")
                    };
                })
                .OrderBy(x => x.discordUsername)
                .Take(250)
                .ToList();

            return Results.Json(new { ok = true, guildId, drivers = users }, JsonWriteOpts);
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

            var key1 = "ANNOUNCEMENTS_CHANNEL_ID_" + guildId;
            var chanStr = (Environment.GetEnvironmentVariable(key1) ??
                           Environment.GetEnvironmentVariable("ANNOUNCEMENTS_CHANNEL_ID") ??
                           "").Trim();

            if (!ulong.TryParse(chanStr, out var chanId))
                return Results.Ok(new { ok = true, guildId, items = Array.Empty<object>() });

            var chan = g.GetTextChannel(chanId);
            if (chan == null)
                return Results.Ok(new { ok = true, guildId, items = Array.Empty<object>() });

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

        app.MapGet("/api/vtc/pair/claim", (HttpRequest req) =>
        {
            var code = (req.Query["code"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                return Results.Json(new { ok = false, error = "MissingCode", message = "Provide ?code=XXXXXX" }, statusCode: 400);

            CleanupExpiredPairs();

            if (!PendingPairs.TryRemove(code, out var p))
                return Results.Json(new { ok = false, error = "InvalidOrExpiredCode" }, statusCode: 404);

            return Results.Ok(new
            {
                ok = true,
                guildId = p.GuildId,
                vtcName = p.VtcName,
                discordUserId = p.DiscordUserId,
                discordUsername = p.DiscordUsername
            });
        });

        // -----------------------------
        // ✅ Dispatch messaging bridge
        // -----------------------------

        // ELD -> Discord (posts into #dispatch)
        app.MapPost("/api/dispatch/send", async (HttpRequest req) =>
        {
            try
            {
                using var reader = new StreamReader(req.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return Results.Json(new { ok = false, error = "EmptyBody" }, statusCode: 400);

                var send = JsonSerializer.Deserialize<DispatchSendRequest>(body, JsonReadOpts);
                if (send == null)
                    return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

                var guildId = (send.GuildId ?? "").Trim();
                var text = (send.Text ?? "").Trim();
                var from = (send.From ?? "").Trim();

                if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(text))
                    return Results.Json(new { ok = false, error = "MissingGuildIdOrText" }, statusCode: 400);

                if (!ulong.TryParse(guildId, out var gid) || _client == null)
                    return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

                var guild = _client.GetGuild(gid);
                if (guild == null)
                    return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

                var chan = ResolveDispatchChannel(guild);
                if (chan == null)
                    return Results.Json(new { ok = false, error = "DispatchChannelNotFound", hint = "Create #dispatch or set DISPATCH_CHANNEL_ID(_GUILDID)" }, statusCode: 404);

                // Prefer webhook so sender shows "dispatch"
                var webhookUrl = GetDispatchWebhookUrl(guildId);
                if (!string.IsNullOrWhiteSpace(webhookUrl))
                {
                    var payload = new
                    {
                        username = "dispatch",
                        content = string.IsNullOrWhiteSpace(from) ? text : $"[{from}] {text}"
                    };

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    using var content = new StringContent(JsonSerializer.Serialize(payload, JsonWriteOpts), Encoding.UTF8, "application/json");
                    var resp = await http.PostAsync(webhookUrl, content);
                    if (!resp.IsSuccessStatusCode)
                        return Results.Json(new { ok = false, error = "WebhookPostFailed", status = (int)resp.StatusCode }, statusCode: 502);

                    return Results.Ok(new { ok = true, mode = "webhook" });
                }

                // Fallback: bot posts (sender shows bot name)
                await chan.SendMessageAsync(string.IsNullOrWhiteSpace(from) ? text : $"[{from}] {text}");
                return Results.Ok(new { ok = true, mode = "bot" });
            }
            catch
            {
                return Results.Json(new { ok = false, error = "ServerError" }, statusCode: 500);
            }
        });

        // Discord -> ELD (ELD polls this)
        // Returns last N messages from #dispatch; sender is always "dispatch"
        app.MapGet("/api/dispatch/inbox", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var takeStr = (req.Query["take"].ToString() ?? "").Trim();
            var afterIdStr = (req.Query["afterId"].ToString() ?? "").Trim(); // optional

            var take = 25;
            if (int.TryParse(takeStr, out var t) && t >= 1 && t <= 100) take = t;

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

            var guild = _client.GetGuild(gid);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var chan = ResolveDispatchChannel(guild);
            if (chan == null)
                return Results.Json(new { ok = false, error = "DispatchChannelNotFound" }, statusCode: 404);

            // Pull messages (requires Read Message History permission)
            var msgs = await chan.GetMessagesAsync(take).FlattenAsync();

            // Filter "afterId" if provided (so ELD can poll incrementally)
            if (ulong.TryParse(afterIdStr, out var afterId) && afterId != 0)
                msgs = msgs.Where(m => m.Id > afterId);

            var items = msgs
                .OrderBy(m => m.Timestamp)
                .Where(m => m.Author != null)
                .Select(m => new
                {
                    id = m.Id.ToString(),
                    sender = "dispatch", // ✅ your requirement
                    // keep actual author so you can display it (or embed it into text)
                    discordAuthor = m.Author.Username ?? "Discord",
                    text = m.Content ?? "",
                    createdUtc = m.Timestamp.UtcDateTime.ToString("O")
                })
                .ToList();

            return Results.Json(new { ok = true, guildId, items }, JsonWriteOpts);
        });

        // -----------------------------
        // Optional: proxy /api/messages/* to HUB (unchanged)
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

            var discordUserId = msg.Author.Id.ToString();
            var discordUsername = msg.Author.Username ?? "";

            CleanupExpiredPairs();
            PendingPairs[code] = new PendingPair(
                guildId,
                vtcName,
                discordUserId,
                discordUsername,
                DateTimeOffset.UtcNow
            );

            await msg.Channel.SendMessageAsync("✅ Link code received. Paste it into the ELD to complete pairing.");
            return;
        }
    }

    // -----------------------------
    // Helpers
    // -----------------------------
    private static SocketTextChannel? ResolveDispatchChannel(SocketGuild guild)
    {
        // 1) Per-guild override: DISPATCH_CHANNEL_ID_<GUILDID>
        // 2) Global override:     DISPATCH_CHANNEL_ID
        // 3) Fallback: channel name "dispatch"
        var gid = guild.Id.ToString();
        var key1 = "DISPATCH_CHANNEL_ID_" + gid;

        var chanStr = (Environment.GetEnvironmentVariable(key1) ??
                       Environment.GetEnvironmentVariable("DISPATCH_CHANNEL_ID") ??
                       "").Trim();

        if (ulong.TryParse(chanStr, out var chanId) && chanId != 0)
        {
            return guild.GetTextChannel(chanId);
        }

        // fallback by name
        return guild.TextChannels.FirstOrDefault(c =>
            string.Equals(c.Name, "dispatch", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDispatchWebhookUrl(string guildId)
    {
        // Per guild: DISPATCH_WEBHOOK_URL_<GUILDID>
        // Global:    DISPATCH_WEBHOOK_URL
        var key1 = "DISPATCH_WEBHOOK_URL_" + guildId;
        return (Environment.GetEnvironmentVariable(key1) ??
                Environment.GetEnvironmentVariable("DISPATCH_WEBHOOK_URL") ??
                "").Trim();
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

    private static void CleanupOldLiveStates()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var kv in Live)
        {
            if (kv.Value.LastSeenUtc < cutoff)
                Live.TryRemove(kv.Key, out _);
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
