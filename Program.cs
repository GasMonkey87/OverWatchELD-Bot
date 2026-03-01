// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Public-release safe
// ✅ Two-way dispatch bridge
// ✅ NEW: Self-service per-guild webhook setup via Discord commands:
//    !setwebhook <url>   (admins only)
//    !testwebhook        (admins only)
//    !clearwebhook       (admins only)
// ✅ Stores per-guild webhooks in guild_webhooks.json on the bot host

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    private const string DefaultHubBase = "https://overwatcheld-saas-production.up.railway.app";
    private static readonly HttpClient HubHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    // ---------- Self-service webhook storage ----------
    private static readonly string WebhooksPath = Path.Combine(AppContext.BaseDirectory, "guild_webhooks.json");
    private static readonly object WebhooksLock = new();
    private static Dictionary<string, string> GuildWebhookUrl = LoadWebhooks();

    private static Dictionary<string, string> LoadWebhooks()
    {
        try
        {
            if (!File.Exists(WebhooksPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(WebhooksPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonReadOpts);
            return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveWebhooks()
    {
        try
        {
            lock (WebhooksLock)
            {
                var json = JsonSerializer.Serialize(GuildWebhookUrl, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(WebhooksPath, json);
            }
        }
        catch { }
    }

    private static string GetWebhookForGuild(string guildId)
    {
        lock (WebhooksLock)
        {
            if (GuildWebhookUrl.TryGetValue(guildId, out var url) && !string.IsNullOrWhiteSpace(url))
                return url.Trim();
        }

        // Optional env fallback if you still want:
        var key1 = "DISPATCH_WEBHOOK_URL_" + guildId;
        return (Environment.GetEnvironmentVariable(key1) ??
                Environment.GetEnvironmentVariable("DISPATCH_WEBHOOK_URL") ??
                "").Trim();
    }

    private static void SetWebhookForGuild(string guildId, string url)
    {
        lock (WebhooksLock)
        {
            GuildWebhookUrl[guildId] = url.Trim();
            SaveWebhooks();
        }
    }

    private static void ClearWebhookForGuild(string guildId)
    {
        lock (WebhooksLock)
        {
            if (GuildWebhookUrl.Remove(guildId))
                SaveWebhooks();
        }
    }

    // ---------- Pairing + roster live state ----------
    private sealed record PendingPair(string GuildId, string VtcName, string DiscordUserId, string DiscordUsername, DateTimeOffset CreatedUtc);
    private static readonly ConcurrentDictionary<string, PendingPair> PendingPairs = new(StringComparer.OrdinalIgnoreCase);

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

    private static readonly ConcurrentDictionary<string, DriverLiveState> Live = new(StringComparer.OrdinalIgnoreCase);

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

    // ---------- Dispatch DTO ----------
    private sealed class DispatchSendRequest
    {
        public string? GuildId { get; set; }
        public string? Text { get; set; }
        public string? From { get; set; }
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

        // Discord client
        var socketConfig = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 50,
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMembers |
                GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages |
                GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(socketConfig);
        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += () => { Console.WriteLine("✅ READY"); return Task.CompletedTask; };
        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // HTTP API
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var hubBase = (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? DefaultHubBase).Trim();
        hubBase = NormalizeAbsoluteBaseUrl(hubBase);
        HubHttp.BaseAddress = new Uri(hubBase + "/", UriKind.Absolute);

        var app = builder.Build();

        app.MapGet("/", () => Results.Ok(new { ok = true, service = "OverWatchELD.VtcBot" }));
        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/build", () => Results.Ok(new { ok = true, build = "VtcBot-2026-02-28-public+dispatch-selfservice-webhooks" }));

        // -------- VTC endpoints --------
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
                return Results.Json(new { error = "MissingGuildId" }, statusCode: 400);

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
                var hb = JsonSerializer.Deserialize<RosterHeartbeat>(body, JsonReadOpts);

                var guildId = (hb?.GuildId ?? "").Trim();
                var userId = (hb?.DiscordUserId ?? "").Trim();

                if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(userId))
                    return Results.Json(new { ok = false, error = "MissingGuildIdOrUserId" }, statusCode: 400);

                var key = $"{guildId}:{userId}";

                Live.AddOrUpdate(key,
                    _ => new DriverLiveState
                    {
                        GuildId = guildId,
                        DiscordUserId = userId,
                        DiscordUsername = (hb?.DiscordUsername ?? "").Trim(),
                        Truck = (hb?.Truck ?? "").Trim(),
                        City = (hb?.City ?? "").Trim(),
                        State = (hb?.State ?? "").Trim(),
                        DutyStatus = (hb?.DutyStatus ?? "").Trim(),
                        LastSeenUtc = DateTimeOffset.UtcNow
                    },
                    (_, existing) =>
                    {
                        var dn = (hb?.DiscordUsername ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(dn)) existing.DiscordUsername = dn;

                        var tr = (hb?.Truck ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(tr)) existing.Truck = tr;

                        var c = (hb?.City ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(c)) existing.City = c;

                        var s = (hb?.State ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(s)) existing.State = s;

                        var ds = (hb?.DutyStatus ?? "").Trim();
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
                return Results.Json(new { error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { error = "GuildNotFound" });

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

                    var hbName = live?.DiscordUsername ?? "";
                    if (!string.IsNullOrWhiteSpace(hbName))
                        username = hbName;

                    var lastSeenUtc = live?.LastSeenUtc ?? DateTimeOffset.MinValue;

                    return new
                    {
                        discordUserId = uid,
                        discordUsername = username,
                        displayName = display,
                        truck = live?.Truck ?? "",
                        city = live?.City ?? "",
                        state = live?.State ?? "",
                        dutyStatus = live?.DutyStatus ?? "",
                        lastSeenUtc = lastSeenUtc == DateTimeOffset.MinValue ? "" : lastSeenUtc.UtcDateTime.ToString("O")
                    };
                })
                .OrderBy(x => x.discordUsername)
                .Take(250)
                .ToList();

            return Results.Json(new { ok = true, guildId, drivers = users }, JsonWriteOpts);
        });

        // -------- Dispatch bridge --------
        app.MapPost("/api/dispatch/send", async (HttpRequest req) =>
        {
            try
            {
                using var reader = new StreamReader(req.Body);
                var body = await reader.ReadToEndAsync();
                var send = JsonSerializer.Deserialize<DispatchSendRequest>(body, JsonReadOpts);

                var guildId = (send?.GuildId ?? "").Trim();
                var text = (send?.Text ?? "").Trim();
                var from = (send?.From ?? "").Trim();

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

                var webhookUrl = GetWebhookForGuild(guildId);

                // Preferred: webhook posts as "dispatch"
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

                // Fallback: bot message
                await chan.SendMessageAsync(string.IsNullOrWhiteSpace(from) ? text : $"[{from}] {text}");
                return Results.Ok(new { ok = true, mode = "bot" });
            }
            catch
            {
                return Results.Json(new { ok = false, error = "ServerError" }, statusCode: 500);
            }
        });

        app.MapGet("/api/dispatch/inbox", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var takeStr = (req.Query["take"].ToString() ?? "").Trim();
            var afterIdStr = (req.Query["afterId"].ToString() ?? "").Trim();

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

            var msgs = await chan.GetMessagesAsync(take).FlattenAsync();

            if (ulong.TryParse(afterIdStr, out var afterId) && afterId != 0)
                msgs = msgs.Where(m => m.Id > afterId);

            var items = msgs
                .OrderBy(m => m.Timestamp)
                .Select(m => new
                {
                    id = m.Id.ToString(),
                    sender = "dispatch",
                    discordAuthor = m.Author?.Username ?? "Discord",
                    text = m.Content ?? "",
                    createdUtc = m.Timestamp.UtcDateTime.ToString("O")
                })
                .ToList();

            return Results.Json(new { ok = true, guildId, items }, JsonWriteOpts);
        });

        // -------- Hub proxy (unchanged) --------
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

        // Admin-only webhook commands
        if (body.StartsWith("setwebhook", StringComparison.OrdinalIgnoreCase))
        {
            if (msg.Channel is not SocketGuildChannel gc)
            {
                await msg.Channel.SendMessageAsync("❌ Use this in a server channel.");
                return;
            }

            if (!IsAdmin(msg))
            {
                await msg.Channel.SendMessageAsync("❌ Admin only.");
                return;
            }

            var url = body.Length > 10 ? body.Substring(10).Trim() : "";
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            {
                await msg.Channel.SendMessageAsync("Usage: `!setwebhook https://discord.com/api/webhooks/...`");
                return;
            }

            SetWebhookForGuild(gc.Guild.Id.ToString(), url);
            await msg.Channel.SendMessageAsync("✅ Webhook saved for this server. Use `!testwebhook` to verify.");
            return;
        }

        if (body.Equals("testwebhook", StringComparison.OrdinalIgnoreCase))
        {
            if (msg.Channel is not SocketGuildChannel gc)
            {
                await msg.Channel.SendMessageAsync("❌ Use this in a server channel.");
                return;
            }

            if (!IsAdmin(msg))
            {
                await msg.Channel.SendMessageAsync("❌ Admin only.");
                return;
            }

            var guildId = gc.Guild.Id.ToString();
            var url = GetWebhookForGuild(guildId);
            if (string.IsNullOrWhiteSpace(url))
            {
                await msg.Channel.SendMessageAsync("❌ No webhook set. Use `!setwebhook <url>`.");
                return;
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var payload = new { username = "dispatch", content = "✅ dispatch webhook test" };
                using var content2 = new StringContent(JsonSerializer.Serialize(payload, JsonWriteOpts), Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(url, content2);

                if (!resp.IsSuccessStatusCode)
                {
                    await msg.Channel.SendMessageAsync($"❌ Webhook test failed (HTTP {(int)resp.StatusCode}).");
                    return;
                }

                await msg.Channel.SendMessageAsync("✅ Webhook test sent.");
            }
            catch
            {
                await msg.Channel.SendMessageAsync("❌ Webhook test failed (network error).");
            }

            return;
        }

        if (body.Equals("clearwebhook", StringComparison.OrdinalIgnoreCase))
        {
            if (msg.Channel is not SocketGuildChannel gc)
            {
                await msg.Channel.SendMessageAsync("❌ Use this in a server channel.");
                return;
            }

            if (!IsAdmin(msg))
            {
                await msg.Channel.SendMessageAsync("❌ Admin only.");
                return;
            }

            ClearWebhookForGuild(gc.Guild.Id.ToString());
            await msg.Channel.SendMessageAsync("✅ Webhook cleared for this server.");
            return;
        }

        // Link pairing
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
            PendingPairs[code] = new PendingPair(guildId, vtcName, discordUserId, discordUsername, DateTimeOffset.UtcNow);

            await msg.Channel.SendMessageAsync("✅ Link code received. Paste it into the ELD to complete pairing.");
            return;
        }
    }

    private static bool IsAdmin(SocketUserMessage msg)
    {
        try
        {
            if (msg.Author is SocketGuildUser gu)
            {
                // Administrator permission OR ManageGuild permission
                return gu.GuildPermissions.Administrator || gu.GuildPermissions.ManageGuild;
            }
        }
        catch { }
        return false;
    }

    private static SocketTextChannel? ResolveDispatchChannel(SocketGuild guild)
    {
        var gid = guild.Id.ToString();
        var key1 = "DISPATCH_CHANNEL_ID_" + gid;

        var chanStr = (Environment.GetEnvironmentVariable(key1) ??
                       Environment.GetEnvironmentVariable("DISPATCH_CHANNEL_ID") ??
                       "").Trim();

        if (ulong.TryParse(chanStr, out var chanId) && chanId != 0)
            return guild.GetTextChannel(chanId);

        return guild.TextChannels.FirstOrDefault(c =>
            string.Equals(c.Name, "dispatch", StringComparison.OrdinalIgnoreCase));
    }

    private static void CleanupExpiredPairs()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var kv in PendingPairs)
            if (kv.Value.CreatedUtc < cutoff)
                PendingPairs.TryRemove(kv.Key, out _);
    }

    private static void CleanupOldLiveStates()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var kv in Live)
            if (kv.Value.LastSeenUtc < cutoff)
                Live.TryRemove(kv.Key, out _);
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
