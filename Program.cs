// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ Commands: !ping, !link CODE, !setdispatchchannel, !setupdispatch, !setwebhook <url>
// ✅ Thread-safe: !setdispatchchannel works in threads (saves parent channel)
// ✅ Webhook URL fix: RestWebhook may not expose .Url -> build url from Id + Token
// ✅ HTTP API:
//    /health, /build
//    /api/vtc/servers
//    /api/vtc/name?guildId=...
//    /api/vtc/roster?guildId=...
//    /api/vtc/pair/claim?code=...
//    /api/messages?guildId=...
//    /api/messages/send?guildId=...

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
using Discord.Rest;
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
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Pairing codes captured from Discord `!link CODE`
    private sealed record PendingPair(
        string GuildId,
        string VtcName,
        string DiscordUserId,
        string DiscordUsername,
        DateTimeOffset CreatedUtc
    );

    private static readonly ConcurrentDictionary<string, PendingPair> PendingPairs = new(StringComparer.OrdinalIgnoreCase);

    // -----------------------------
    // Per-guild config (stored locally on bot container)
    // -----------------------------
    private sealed class GuildConfig
    {
        public string GuildId { get; set; } = "";
        public string DispatchChannelId { get; set; } = "";     // text channel id
        public string DispatchWebhookUrl { get; set; } = "";    // webhook url
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private static readonly object CfgLock = new();
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "guild_config.json");

    private static List<GuildConfig> LoadCfg()
    {
        lock (CfgLock)
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new List<GuildConfig>();
                return JsonSerializer.Deserialize<List<GuildConfig>>(File.ReadAllText(ConfigPath), JsonReadOpts)
                       ?? new List<GuildConfig>();
            }
            catch { return new List<GuildConfig>(); }
        }
    }

    private static void SaveCfg(List<GuildConfig> cfg)
    {
        lock (CfgLock)
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            }
            catch { }
        }
    }

    private static GuildConfig GetOrCreateGuildCfg(string guildId)
    {
        var all = LoadCfg();
        var ex = all.FirstOrDefault(x => x.GuildId == guildId);
        if (ex != null) return ex;

        ex = new GuildConfig { GuildId = guildId, UpdatedUtc = DateTimeOffset.UtcNow };
        all.Add(ex);
        SaveCfg(all);
        return ex;
    }

    private static void UpsertGuildCfg(GuildConfig updated)
    {
        var all = LoadCfg();
        var ex = all.FirstOrDefault(x => x.GuildId == updated.GuildId);
        if (ex == null) all.Add(updated);
        else
        {
            if (!string.IsNullOrWhiteSpace(updated.DispatchChannelId))
                ex.DispatchChannelId = updated.DispatchChannelId;

            if (!string.IsNullOrWhiteSpace(updated.DispatchWebhookUrl))
                ex.DispatchWebhookUrl = updated.DispatchWebhookUrl;

            ex.UpdatedUtc = DateTimeOffset.UtcNow;
        }
        SaveCfg(all);
    }

    // -----------------------------
    // Messaging payload
    // -----------------------------
    private sealed class SendReq
    {
        public string? DriverName { get; set; }
        public string Text { get; set; } = "";
        public string? Source { get; set; }
    }

    // -----------------------------
    // Main
    // -----------------------------
    public static async Task Main(string[] args)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN (or BOT_TOKEN).");
            return;
        }

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

        var hubBase = (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? DefaultHubBase).Trim();
        hubBase = NormalizeAbsoluteBaseUrl(hubBase);
        HubHttp.BaseAddress = new Uri(hubBase + "/", UriKind.Absolute);

        var app = builder.Build();

        app.MapGet("/", () => Results.Ok(new { ok = true, service = "OverWatchELD.VtcBot" }));
        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/build", () => Results.Ok(new { ok = true, build = "VtcBot-2026-02-28-dispatch-webhookurlfix" }));

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

        app.MapGet("/api/vtc/name", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { error = "GuildNotFound" });

            try { await g.DownloadUsersAsync(); } catch { }

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

            try { await g.DownloadUsersAsync(); } catch { }

            var users = g.Users
                .Where(u => !u.IsBot)
                .Select(u => new
                {
                    id = u.Id.ToString(),
                    name = (u.Nickname ?? u.Username) ?? (u.Username ?? "")
                })
                .OrderBy(x => x.name)
                .Take(500)
                .ToList();

            return Results.Ok(new { ok = true, guildId, drivers = users });
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
        // Dispatch messaging endpoints (ELD Dispatch Window)
        // -----------------------------
        app.MapGet("/api/messages", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { ok = false, error = "GuildNotFound" });

            var cfg = GetOrCreateGuildCfg(guildId);
            if (!ulong.TryParse(cfg.DispatchChannelId, out var chanId))
                return Results.Ok(new { ok = true, guildId, items = Array.Empty<object>() });

            var chan = g.GetTextChannel(chanId);
            if (chan == null)
                return Results.Ok(new { ok = true, guildId, items = Array.Empty<object>() });

            var msgs = await chan.GetMessagesAsync(50).FlattenAsync();

            var items = msgs
                .OrderBy(m => m.Timestamp)
                .Select(m =>
                {
                    var isBot = m.Author?.IsBot ?? false;
                    var src = isBot ? "eld" : "discord";
                    var driverName = isBot ? "driver" : (m.Author?.Username ?? "dispatch");

                    return new
                    {
                        id = m.Id.ToString(),
                        guildId,
                        driverName,
                        text = m.Content ?? "",
                        source = src,
                        createdUtc = m.Timestamp.UtcDateTime.ToString("O")
                    };
                })
                .ToList();

            return Results.Json(new { ok = true, guildId, items }, JsonWriteOpts);
        });

        app.MapPost("/api/messages/send", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(guildId, out var gid) || _client == null)
                return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

            var g = _client.GetGuild(gid);
            if (g == null) return Results.NotFound(new { ok = false, error = "GuildNotFound" });

            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body))
                return Results.Json(new { ok = false, error = "EmptyBody" }, statusCode: 400);

            SendReq? payload;
            try { payload = JsonSerializer.Deserialize<SendReq>(body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Text))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var cfg = GetOrCreateGuildCfg(guildId);
            if (!ulong.TryParse(cfg.DispatchChannelId, out var chanId))
                return Results.Json(new { ok = false, error = "DispatchChannelNotConfigured", hint = "Run !setdispatchchannel in your dispatch channel." }, statusCode: 409);

            var chan = g.GetTextChannel(chanId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "DispatchChannelNotFound" }, statusCode: 404);

            var sender = string.IsNullOrWhiteSpace(payload.DriverName) ? "driver" : payload.DriverName.Trim();
            var text = payload.Text.Trim();

            // Prefer webhook (clean sender name)
            if (!string.IsNullOrWhiteSpace(cfg.DispatchWebhookUrl))
            {
                try
                {
                    await SendViaWebhookAsync(cfg.DispatchWebhookUrl, sender, text);
                    return Results.Ok(new { ok = true });
                }
                catch
                {
                    // fallback to bot message below
                }
            }

            await chan.SendMessageAsync($"**{sender}:** {text}");
            return Results.Ok(new { ok = true });
        });

        // Start HTTP server
        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");

        await Task.Delay(-1);
    }

    // -----------------------------
    // Discord commands
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage raw)
    {
        if (raw is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        var body = content[1..].Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        // ✅ Thread-safe: works in channel OR thread (thread saves parent channel)
        if (body.Equals("setdispatchchannel", StringComparison.OrdinalIgnoreCase))
        {
            SocketGuild? guild = null;
            ulong? channelId = null;
            string channelLabel = "";

            if (msg.Channel is SocketTextChannel tc)
            {
                guild = tc.Guild;
                channelId = tc.Id;
                channelLabel = "#" + tc.Name;
            }
            else if (msg.Channel is SocketThreadChannel th)
            {
                guild = th.Guild;
                channelId = th.ParentChannel?.Id;
                channelLabel = th.ParentChannel != null ? ("#" + th.ParentChannel.Name + " (from thread)") : "(thread parent missing)";
            }

            if (guild == null || channelId == null)
            {
                await msg.Channel.SendMessageAsync("❌ Run this in a server channel (or a thread inside a server).");
                return;
            }

            var guildId = guild.Id.ToString();
            var cfg = GetOrCreateGuildCfg(guildId);
            cfg.DispatchChannelId = channelId.Value.ToString();
            cfg.UpdatedUtc = DateTimeOffset.UtcNow;
            UpsertGuildCfg(cfg);

            await msg.Channel.SendMessageAsync($"✅ Dispatch channel saved for this server: {channelLabel}");
            return;
        }

        // ✅ Auto-create webhook (must run in the actual text channel, not thread)
        if (body.Equals("setupdispatch", StringComparison.OrdinalIgnoreCase))
        {
            if (msg.Channel is not SocketTextChannel tc)
            {
                await msg.Channel.SendMessageAsync("❌ Run `!setupdispatch` in the actual dispatch text channel (not a thread).");
                return;
            }

            var guildId = tc.Guild.Id.ToString();
            var cfg = GetOrCreateGuildCfg(guildId);
            cfg.DispatchChannelId = tc.Id.ToString();

            try
            {
                RestWebhook wh = await tc.CreateWebhookAsync("OverWatchELD Dispatch");

                // ✅ Fix: RestWebhook may not have .Url in your Discord.Net version
                // Build URL from id + token
                var webhookUrl = $"https://discord.com/api/webhooks/{wh.Id}/{wh.Token}";

                cfg.DispatchWebhookUrl = webhookUrl;
                cfg.UpdatedUtc = DateTimeOffset.UtcNow;
                UpsertGuildCfg(cfg);

                await msg.Channel.SendMessageAsync("✅ Dispatch configured: channel + webhook saved.");
                return;
            }
            catch
            {
                cfg.UpdatedUtc = DateTimeOffset.UtcNow;
                UpsertGuildCfg(cfg);

                await msg.Channel.SendMessageAsync("⚠️ Channel saved, but webhook creation failed. Give the bot **Manage Webhooks** or use `!setwebhook <url>`.");
                return;
            }
        }

        // ✅ Manual webhook set
        if (body.StartsWith("setwebhook", StringComparison.OrdinalIgnoreCase))
        {
            SocketGuild? guild = null;
            if (msg.Channel is SocketGuildChannel gc) guild = gc.Guild;

            if (guild == null)
            {
                await msg.Channel.SendMessageAsync("❌ Run this in a server channel (not DM).");
                return;
            }

            var url = body.Length > "setwebhook".Length ? body["setwebhook".Length..].Trim() : "";
            if (string.IsNullOrWhiteSpace(url) || (!url.StartsWith("http://") && !url.StartsWith("https://")))
            {
                await msg.Channel.SendMessageAsync("Usage: `!setwebhook https://discord.com/api/webhooks/...`");
                return;
            }

            var guildId = guild.Id.ToString();
            var cfg = GetOrCreateGuildCfg(guildId);
            cfg.DispatchWebhookUrl = url;
            cfg.UpdatedUtc = DateTimeOffset.UtcNow;
            UpsertGuildCfg(cfg);

            await msg.Channel.SendMessageAsync("✅ Webhook saved for this server.");
            return;
        }

        // Pairing: user types !link CODE
        if (body.StartsWith("link", StringComparison.OrdinalIgnoreCase))
        {
            var code = body.Length > 4 ? body[4..].Trim() : "";
            if (string.IsNullOrWhiteSpace(code))
            {
                await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`");
                return;
            }

            if (msg.Channel is not SocketGuildChannel gc)
            {
                await msg.Channel.SendMessageAsync("❌ This command must be used in a server channel (not DM).");
                return;
            }

            var guildId = gc.Guild.Id.ToString();
            var vtcName = gc.Guild.Name ?? "";

            var discordUserId = msg.Author.Id.ToString();
            var discordUsername = msg.Author.Username ?? "";

            CleanupExpiredPairs();
            PendingPairs[code] = new PendingPair(guildId, vtcName, discordUserId, discordUsername, DateTimeOffset.UtcNow);

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

    private static async Task SendViaWebhookAsync(string webhookUrl, string username, string content)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var payload = new { username, content };
        var json = JsonSerializer.Serialize(payload, JsonWriteOpts);
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await http.PostAsync(webhookUrl, body);
        resp.EnsureSuccessStatusCode();
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
