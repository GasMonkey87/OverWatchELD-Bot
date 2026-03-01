// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ Commands: !ping, !setdispatchchannel
// ✅ NEW: Option A auto-thread per user under dispatch channel (via /api/messages/send)
// ✅ NEW: Option B manual override: !linkthread (run inside thread)

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

// Thread routing patch namespace
using OverWatchELD.VtcBot.Threads;

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

    // Thread map store (key-based: "{guildId}:{discordUserId}" -> threadId)
    private static ThreadMapStore? _threadStore;

    // Pairing codes captured from Discord `!link CODE` (left intact even if not used in this excerpt)
    private sealed record PendingPair(
        string GuildId,
        string VtcName,
        string DiscordUserId,
        string DiscordUsername,
        DateTimeOffset CreatedUtc
    );

    private static readonly ConcurrentDictionary<string, PendingPair> PendingPairs = new();
    private static readonly ConcurrentDictionary<string, GuildCfg> GuildCfgs = new();

    // Storage folder (Railway container-safe)
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string GuildCfgPath = Path.Combine(DataDir, "guild_cfg.json");

    // Thread map file
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

    // -----------------------------
    // Guild configuration
    // -----------------------------
    private sealed class GuildCfg
    {
        public string GuildId { get; set; } = "";
        public string DispatchChannelId { get; set; } = "";
        public string DispatchWebhookUrl { get; set; } = "";
        public string VtcName { get; set; } = "";
    }

    // -----------------------------
    // HTTP payload models
    // -----------------------------
    private sealed class SendReq
    {
        public string? Text { get; set; }
        public string? DriverName { get; set; }          // legacy
        public string? DiscordUserId { get; set; }       // preferred
        public string? DiscordUsername { get; set; }     // hint
        public string? Source { get; set; }              // optional
    }

    private sealed class MsgItem
    {
        public string Id { get; set; } = "";
        public string From { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);

        // Init thread map store
        _threadStore = new ThreadMapStore(ThreadMapPath);

        // Discord client
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
                GatewayIntents.GuildMembers // helps resolve users for thread invites
        });

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {msg.Severity,-7} {msg.Source,-12} {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Load configs
        LoadGuildCfgs();

        // -----------------------------
        // HTTP server (Minimal API)
        // -----------------------------
        var portStr = (Environment.GetEnvironmentVariable("PORT") ?? "8080").Trim();
        if (!int.TryParse(portStr, out var port)) port = 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/build", () =>
        {
            var sha = (Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA") ?? "").Trim();
            return Results.Ok(new { ok = true, sha });
        });

        // -----------------------------
        // VTC APIs (kept minimal here)
        // -----------------------------
        app.MapGet("/api/vtc/servers", () =>
        {
            var servers = _client?.Guilds.Select(g => new
            {
                guildId = g.Id.ToString(),
                name = g.Name
            }).ToArray() ?? Array.Empty<object>();

            return Results.Json(new { ok = true, servers }, JsonWriteOpts);
        });

        app.MapGet("/api/vtc/name", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            var cfg = GetOrCreateGuildCfg(guildId);
            return Results.Json(new { ok = true, guildId, vtcName = cfg.VtcName ?? "" }, JsonWriteOpts);
        });

        // -----------------------------
        // Dispatch: GET /api/messages?guildId=...
        // Reads recent messages from parent dispatch channel
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
                return Results.Json(new
                {
                    ok = false,
                    error = "DispatchChannelNotConfigured",
                    hint = "Run !setdispatchchannel in your dispatch channel."
                }, statusCode: 409);

            var chan = g.GetTextChannel(chanId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "DispatchChannelNotFound" }, statusCode: 404);

            var msgs = await chan.GetMessagesAsync(limit: 50).FlattenAsync();

            var items = msgs
                .OrderBy(m => m.Timestamp)
                .Select(m => new MsgItem
                {
                    Id = m.Id.ToString(),
                    From = m.Author?.Username ?? "Dispatch",
                    Text = m.Content ?? "",
                    CreatedUtc = m.Timestamp.UtcDateTime
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId, items }, JsonWriteOpts);
        });

        // -----------------------------
        // Dispatch: POST /api/messages/send?guildId=...
        // ✅ Routes to per-user thread (Option A)
        // -----------------------------
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

            var discordUserId = (payload.DiscordUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(discordUserId))
                return Results.Json(new { ok = false, error = "MissingDiscordUserId" }, statusCode: 400);

            var cfg = GetOrCreateGuildCfg(guildId);
            if (!ulong.TryParse(cfg.DispatchChannelId, out var dispatchChanId))
                return Results.Json(new
                {
                    ok = false,
                    error = "DispatchChannelNotConfigured",
                    hint = "Run !setdispatchchannel in your dispatch channel."
                }, statusCode: 409);

            if (_threadStore == null)
                return Results.Json(new { ok = false, error = "ThreadStoreNotReady" }, statusCode: 500);

            var text = payload.Text.Trim();

            // Build router for this guild's dispatch channel
            var router = new DiscordThreadRouter(_client, _threadStore, dispatchChanId);

            try
            {
                await router.SendToUserThreadAsync(guildId, discordUserId, text);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Start HTTP server
        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");

        await Task.Delay(-1);
    }

    // -----------------------------
    // Discord commands
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (_client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        // ✅ Option B: manual override for routing (run inside thread)
        // This is handled FIRST and does not require "!" prefix if your LinkThreadCommand checks it.
        if (_threadStore != null)
        {
            var handled = await OverWatchELD.VtcBot.Threads.LinkThreadCommand.TryHandleAsync(msg, _client, _threadStore);
            if (handled) return;
        }

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        var body = content[1..].Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        // Thread-safe: works in channel OR thread (thread saves parent channel)
        if (body.Equals("setdispatchchannel", StringComparison.OrdinalIgnoreCase))
        {
            SocketGuild? guild = null;
            ulong? channelId = null;
            string channelLabel = "";

            if (msg.Channel is SocketTextChannel tc)
            {
                guild = tc.Guild;
                channelId = tc.Id;
                channelLabel = $"#{tc.Name}";
            }
            else if (msg.Channel is SocketThreadChannel th)
            {
                guild = th.Guild;
                channelId = th.ParentChannel?.Id;
                channelLabel = th.ParentChannel != null ? $"#{th.ParentChannel.Name} (parent of thread)" : "(parent unknown)";
            }

            if (guild == null || channelId == null)
            {
                await msg.Channel.SendMessageAsync("❌ Unable to detect guild/channel. Run in a server channel.");
                return;
            }

            var cfg = GetOrCreateGuildCfg(guild.Id.ToString());
            cfg.DispatchChannelId = channelId.Value.ToString();
            SaveGuildCfgs();

            await msg.Channel.SendMessageAsync($"✅ Dispatch channel saved: {channelLabel}");
            return;
        }
    }

    // -----------------------------
    // Guild config persistence
    // -----------------------------
    private static GuildCfg GetOrCreateGuildCfg(string guildId)
    {
        guildId = (guildId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(guildId)) guildId = "unknown";
        return GuildCfgs.GetOrAdd(guildId, id => new GuildCfg { GuildId = id });
    }

    private static void LoadGuildCfgs()
    {
        try
        {
            if (!File.Exists(GuildCfgPath)) return;

            var json = File.ReadAllText(GuildCfgPath);
            var list = JsonSerializer.Deserialize<List<GuildCfg>>(json, JsonReadOpts) ?? new List<GuildCfg>();

            foreach (var cfg in list)
            {
                if (cfg == null) continue;
                var id = (cfg.GuildId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;
                GuildCfgs[id] = cfg;
            }
        }
        catch { }
    }

    private static void SaveGuildCfgs()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            var list = GuildCfgs.Values
                .OrderBy(x => x.GuildId)
                .ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GuildCfgPath, json);
        }
        catch { }
    }

    // -----------------------------
    // Webhook helper (unchanged)
    // -----------------------------
    private static async Task SendViaWebhookAsync(string webhookUrl, string username, string content)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            username = username,
            content = content
        };

        var json = JsonSerializer.Serialize(payload, JsonWriteOpts);
        using var sc = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await http.PostAsync(webhookUrl, sc);
        resp.EnsureSuccessStatusCode();
    }
}
