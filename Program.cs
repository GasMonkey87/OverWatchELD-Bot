// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ FIXES “messages not going through” for your current ELD build:
//    - Implements endpoints ELD actually calls (BotApiService.cs):
//        GET  /api/messages
//        POST /api/messages/send
//        GET  /api/messages/thread/byuser
//        POST /api/messages/thread/send/byuser
//        POST /api/messages/thread/sendform/byuser
//        DELETE /api/messages/delete (+ bulk)  (already used by ELD)
// ✅ Thread-based Dispatch: Discord private thread per driver (mapped by guildId+discordUserId)
// ✅ Admin commands (Discord):
//    - !setdispatchchannel #channel
//    - !setupdispatch #channel              (creates webhook URL safely, no RestWebhook.Url)
//    - !setdispatchwebhook <url>
//    - !announcement #channel               (creates + saves ANNOUNCEMENTS webhook)
//    - !setannouncementwebhook <url>
// ✅ ELD Announcements:
//    - GET  /api/vtc/announcements?guildId=...
//    - POST /api/vtc/announcements/post     (ELD -> Discord announcements via webhook)
// ✅ IMPORTANT: Webhook-authored messages are NO LONGER ignored.
//    (We ignore only our own bot user id, not msg.Author.IsBot)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

// Thread routing patch namespace
using OverWatchELD.VtcBot.Threads;

internal static class Program
{
    private static DiscordSocketClient? _client;

    // ✅ Gate API calls until Discord gateway cache is ready
    private static volatile bool _discordReady = false;

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    // Storage folder (Railway container-safe)
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    // Thread map store (key-based: "{guildId}:{discordUserId}" -> threadId)
    private static ThreadMapStore? _threadStore;
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

    // If ThreadMapStore doesn't expose get/set APIs in your build, we still link using this fallback:
    private static readonly string ThreadMapFallbackPath = Path.Combine(DataDir, "thread_map_fallback.json");

    // Dispatch + Announcement settings
    private static readonly string DispatchCfgPath = Path.Combine(DataDir, "dispatch_settings.json");
    private static DispatchSettingsStore? _dispatchStore;

    // -----------------------------
    // HTTP payload models (ELD expects these shapes)
    // -----------------------------
    private sealed class MessagesResponse
    {
        public bool Ok { get; set; }
        public string? GuildId { get; set; }
        public List<MessageDto>? Items { get; set; }
    }

    private sealed class MessageDto
    {
        public string Id { get; set; } = "";
        public string GuildId { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class SendMessageReq
    {
        public string? DriverName { get; set; }
        public string? DiscordUserId { get; set; }
        public string? DiscordUsername { get; set; }
        public string Text { get; set; } = "";
        public string? Source { get; set; }
    }

    private sealed class ThreadMessagesResponse
    {
        public bool Ok { get; set; }
        public string? GuildId { get; set; }
        public string? ThreadId { get; set; }
        public List<ThreadMessageDto>? Items { get; set; }
    }

    private sealed class ThreadMessageDto
    {
        public string Id { get; set; } = "";
        public string From { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public List<string>? Attachments { get; set; }
    }

    private sealed class MarkReq
    {
        public string? ChannelId { get; set; }
        public string? MessageId { get; set; }
    }

    private sealed class MarkBulkReq
    {
        public string? ChannelId { get; set; }
        public List<string>? MessageIds { get; set; }
    }

    private sealed class DeleteReq
    {
        public string? ChannelId { get; set; }
        public string? MessageId { get; set; }
    }

    private sealed class DeleteBulkReq
    {
        public string? ChannelId { get; set; }
        public List<string>? MessageIds { get; set; }
    }

    private sealed class VtcAnnouncementPostReq
    {
        public string? GuildId { get; set; }
        public string? Text { get; set; }
        public string? Author { get; set; }
    }

    // -----------------------------
    // Settings store (guild-scoped)
    // -----------------------------
    private sealed class DispatchSettings
    {
        public string GuildId { get; set; } = "";
        public string? DispatchChannelId { get; set; }        // text channel id
        public string? DispatchWebhookUrl { get; set; }       // optional
        public string? AnnouncementChannelId { get; set; }    // text channel id
        public string? AnnouncementWebhookUrl { get; set; }   // for ELD -> Discord announcements mirroring
    }

    private sealed class DispatchSettingsStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, DispatchSettings> _byGuild = new();

        public DispatchSettingsStore(string path)
        {
            _path = path;
            Load();
        }

        public DispatchSettings Get(string guildId)
        {
            lock (_lock)
            {
                if (!_byGuild.TryGetValue(guildId, out var s))
                {
                    s = new DispatchSettings { GuildId = guildId };
                    _byGuild[guildId] = s;
                    Save();
                }
                return s;
            }
        }

        public void SetDispatchChannel(string guildId, ulong channelId)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchChannelId = channelId.ToString();
                Save();
            }
        }

        public void SetDispatchWebhook(string guildId, string webhookUrl)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchWebhookUrl = webhookUrl;
                Save();
            }
        }

        public void SetAnnouncementChannel(string guildId, ulong channelId)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementChannelId = channelId.ToString();
                Save();
            }
        }

        public void SetAnnouncementWebhook(string guildId, string webhookUrl)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementWebhookUrl = webhookUrl;
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _byGuild = new(); return; }
                var json = File.ReadAllText(_path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, DispatchSettings>>(json, JsonReadOpts);
                _byGuild = dict ?? new();
            }
            catch { _byGuild = new(); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                var json = JsonSerializer.Serialize(_byGuild, JsonWriteOpts);
                File.WriteAllText(_path, json);
            }
            catch { }
        }
    }

    // -----------------------------
    // Thread map fallback store (guild+user -> threadId)
    // -----------------------------
    private sealed class ThreadMapFallback
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, string> _map = new(); // key: "{guildId}:{userId}" => threadId string

        public ThreadMapFallback(string path)
        {
            _path = path;
            Load();
        }

        public ulong TryGet(ulong guildId, ulong userId)
        {
            lock (_lock)
            {
                var key = $"{guildId}:{userId}";
                if (_map.TryGetValue(key, out var v) && ulong.TryParse(v, out var tid) && tid != 0)
                    return tid;
                return 0;
            }
        }

        public void Set(ulong guildId, ulong userId, ulong threadId)
        {
            lock (_lock)
            {
                var key = $"{guildId}:{userId}";
                _map[key] = threadId.ToString();
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _map = new(); return; }
                var json = File.ReadAllText(_path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonReadOpts);
                _map = dict ?? new();
            }
            catch { _map = new(); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                var json = JsonSerializer.Serialize(_map, JsonWriteOpts);
                File.WriteAllText(_path, json);
            }
            catch { }
        }
    }

    private static ThreadMapFallback? _threadFallback;

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);

        _threadStore = new ThreadMapStore(ThreadMapPath);
        _threadFallback = new ThreadMapFallback(ThreadMapFallbackPath);
        _dispatchStore = new DispatchSettingsStore(DispatchCfgPath);

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
                GatewayIntents.GuildMembers
        });

        _client.Ready += () =>
        {
            _discordReady = true;
            Console.WriteLine("✅ Discord client READY (guild cache loaded).");
            return Task.CompletedTask;
        };

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {msg.Severity,-7} {msg.Source,-12} {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

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
        // Servers list
        // -----------------------------
        app.MapGet("/api/vtc/servers", () =>
        {
            var client = _client;
            if (client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var servers = client.Guilds.Select(g => new
            {
                guildId = g.Id.ToString(),
                name = g.Name,
                serverName = g.Name
            }).ToArray();

            return Results.Json(new { ok = true, servers, serverCount = servers.Length }, JsonWriteOpts);
        });

        // Compatibility aliases
        app.MapGet("/api/servers", () => Results.Redirect("/api/vtc/servers", permanent: false));
        app.MapGet("/api/discord/servers", () => Results.Redirect("/api/vtc/servers", permanent: false));
        app.MapGet("/api/vtc/guilds", () => Results.Redirect("/api/vtc/servers", permanent: false));

        app.MapGet("/api/vtc/status", () => Results.Json(new
        {
            ok = _client != null && _discordReady,
            discordReady = _discordReady,
            guildCount = _client?.Guilds.Count ?? 0
        }, JsonWriteOpts));

        app.MapGet("/api/status", () => Results.Redirect("/api/vtc/status", permanent: false));
        app.MapGet("/api/discord/status", () => Results.Redirect("/api/vtc/status", permanent: false));

        app.MapGet("/api/vtc/name", (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            SocketGuild? g = null;

            if (!string.IsNullOrWhiteSpace(guildIdStr) && ulong.TryParse(guildIdStr, out var gid))
                g = _client.Guilds.FirstOrDefault(x => x.Id == gid);

            g ??= _client.Guilds.FirstOrDefault();

            return Results.Json(new
            {
                ok = g != null,
                guildId = g?.Id.ToString() ?? "",
                name = g?.Name ?? "Not Connected",
                vtcName = g?.Name ?? "Not Connected"
            }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ ELD Announcements: GET (Discord -> ELD polling)
        // Endpoint used by ELD: /api/vtc/announcements?guildId=...
        // Returns: { ok:true, announcements:[{ text, author, createdUtc, attachments }] }
        // -----------------------------
        app.MapGet("/api/vtc/announcements", async (HttpRequest req) =>
        {
            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var limitStr = (req.Query["limit"].ToString() ?? "25").Trim();
            if (!int.TryParse(limitStr, out var limit)) limit = 25;
            limit = Math.Clamp(limit, 1, 100);

            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            if (string.IsNullOrWhiteSpace(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            var guild = _client.Guilds.FirstOrDefault(g => g.Id == guildId);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guildIdStr);
            if (settings == null || !ulong.TryParse(settings.AnnouncementChannelId, out var annChannelId) || annChannelId == 0)
                return Results.Json(new { ok = false, error = "AnnouncementChannelNotSet" }, statusCode: 400);

            var chan = guild.GetTextChannel(annChannelId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "AnnouncementChannelNotFound" }, statusCode: 404);

            var msgs = await chan.GetMessagesAsync(limit: limit).FlattenAsync();

            var announcements = msgs
                .OrderByDescending(m => m.Timestamp)
                .Select(m =>
                {
                    var attachments = new List<string>();
                    try
                    {
                        foreach (var a in m.Attachments)
                            if (!string.IsNullOrWhiteSpace(a?.Url))
                                attachments.Add(a.Url);

                        foreach (var e in m.Embeds)
                            if (!string.IsNullOrWhiteSpace(e?.Url))
                                attachments.Add(e.Url);
                    }
                    catch { }

                    return new
                    {
                        text = (m.Content ?? "").Trim(),
                        author = (m.Author?.Username ?? "Announcements").Trim(),
                        createdUtc = m.Timestamp.UtcDateTime,
                        attachments
                    };
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId = guildIdStr, announcements }, JsonWriteOpts);
        });

        // Aliases (safe)
        app.MapGet("/api/announcements", () => Results.Redirect("/api/vtc/announcements", permanent: false));
        app.MapGet("/api/messages/announcements", () => Results.Redirect("/api/vtc/announcements", permanent: false));
        app.MapGet("/api/eld/announcements", () => Results.Redirect("/api/vtc/announcements", permanent: false));

        // -----------------------------
        // ✅ ELD Announcements: POST (ELD -> Discord via webhook)
        // ELD local dashboard posts to /api/vtc/announcements/post in its own host;
        // but your ELD can also call THIS bot endpoint to mirror to Discord.
        // Body: { guildId, text, author }
        // -----------------------------
        app.MapPost("/api/vtc/announcements/post", async (HttpRequest req) =>
        {
            VtcAnnouncementPostReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<VtcAnnouncementPostReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            var guildIdStr = (payload?.GuildId ?? "").Trim();
            var text = (payload?.Text ?? "").Trim();
            var author = (payload?.Author ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildIdStr))
                guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildIdStr))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "EmptyAnnouncement" }, statusCode: 400);

            var settings = _dispatchStore?.Get(guildIdStr);
            var hookUrl = (settings?.AnnouncementWebhookUrl ?? "").Trim();

            if (string.IsNullOrWhiteSpace(hookUrl))
                return Results.Json(new { ok = false, error = "AnnouncementWebhookNotSet" }, statusCode: 400);

            // Send to Discord webhook (incoming)
            try
            {
                var content = string.IsNullOrWhiteSpace(author) ? text : $"**{author}:** {text}";
                var webhookPayload = new
                {
                    username = "OverWatch ELD",
                    content
                };

                var json = JsonSerializer.Serialize(webhookPayload, JsonWriteOpts);
                using var sc = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(hookUrl, sc);

                var respBody = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { ok = false, error = "WebhookSendFailed", status = (int)resp.StatusCode, body = respBody }, statusCode: 502);

                return Results.Json(new { ok = true }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "WebhookSendException", message = ex.Message }, statusCode: 500);
            }
        });

        // -----------------------------
        // ✅ Messages list (legacy) — ELD may call this; keep it non-breaking.
        // GET /api/messages?guildId=...&driverName=...
        // We return a minimal stream of recent thread messages for the linked driver (if linked).
        // -----------------------------
        app.MapGet("/api/messages", async (HttpRequest req) =>
        {
            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var driverName = (req.Query["driverName"].ToString() ?? "").Trim();
            var discordUserId = (req.Query["discordUserId"].ToString() ?? "").Trim(); // optional

            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            if (string.IsNullOrWhiteSpace(guildIdStr))
            {
                // allow older single-server builds (pick first)
                var g0 = _client.Guilds.FirstOrDefault();
                guildIdStr = g0?.Id.ToString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            // If discordUserId provided, try thread; otherwise return empty list.
            var items = new List<MessageDto>();

            if (ulong.TryParse(discordUserId, out var duid) && duid != 0)
            {
                var tid = ThreadStoreTryGet(guildId, duid);
                if (tid != 0)
                {
                    var chan = await ResolveChannelAsync(tid);
                    if (chan != null)
                    {
                        await EnsureThreadOpenAsync(chan);
                        var msgs = await chan.GetMessagesAsync(limit: 25).FlattenAsync();

                        foreach (var m in msgs.OrderByDescending(x => x.Timestamp))
                        {
                            items.Add(new MessageDto
                            {
                                Id = m.Id.ToString(),
                                GuildId = guildIdStr,
                                DriverName = string.IsNullOrWhiteSpace(driverName) ? (m.Author?.Username ?? "Dispatch") : driverName,
                                Text = m.Content ?? "",
                                Source = "discord",
                                CreatedUtc = m.Timestamp.UtcDateTime
                            });
                        }
                    }
                }
            }

            var respObj = new MessagesResponse { Ok = true, GuildId = guildIdStr, Items = items };
            return Results.Json(respObj, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ ELD -> Bot send (THIS is what your ELD calls)
        // POST /api/messages/send?guildId=...
        // body: { driverName, discordUserId, discordUsername, text, source }
        // -----------------------------
        app.MapPost("/api/messages/send", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();

            SendMessageReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<SendMessageReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Text))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var text = payload.Text.Trim();
            var discordUserIdStr = (payload.DiscordUserId ?? "").Trim();
            var driverName = (payload.DriverName ?? "").Trim();
            var discordUsername = (payload.DiscordUsername ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildIdStr))
            {
                // allow older single-server ELD builds
                var g0 = _client.Guilds.FirstOrDefault();
                guildIdStr = g0?.Id.ToString() ?? "";
            }

            if (!ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            if (!ulong.TryParse(discordUserIdStr, out var discordUserId) || discordUserId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadDiscordUserId" }, statusCode: 400);

            // Ensure thread exists (auto-create under dispatch channel if needed)
            var threadId = ThreadStoreTryGet(guildId, discordUserId);
            if (threadId == 0)
            {
                var created = await EnsureDriverThreadAsync(guildId, discordUserId, discordUsername);
                if (created == 0)
                    return Results.Json(new { ok = false, error = "NoDispatchChannelOrThreadCreateFailed" }, statusCode: 500);

                threadId = created;
            }

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await EnsureThreadOpenAsync(chan);

            // Format sender safely (don’t leak personal Discord name; use provided driverName/discordUsername)
            var who = !string.IsNullOrWhiteSpace(driverName) ? driverName :
                      !string.IsNullOrWhiteSpace(discordUsername) ? discordUsername :
                      "Driver";

            var msgText = $"**{who}:** {text}";

            try
            {
                var sent = await chan.SendMessageAsync(msgText);
                return Results.Json(new { ok = true, threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[api/messages/send] ❌ {ex}");
                return Results.Json(new { ok = false, error = "SendFailed", message = ex.Message }, statusCode: 500);
            }
        });

        // -----------------------------
        // ✅ Thread state by user (THIS is what ELD polls now)
        // GET /api/messages/thread/byuser?guildId=...&discordUserId=...&limit=50
        // -----------------------------
        app.MapGet("/api/messages/thread/byuser", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var discordUserIdStr = (req.Query["discordUserId"].ToString() ?? "").Trim();
            var limitStr = (req.Query["limit"].ToString() ?? "50").Trim();

            if (!int.TryParse(limitStr, out var limit)) limit = 50;
            limit = Math.Clamp(limit, 1, 100);

            if (!ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            if (!ulong.TryParse(discordUserIdStr, out var discordUserId) || discordUserId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadDiscordUserId" }, statusCode: 400);

            var threadId = ThreadStoreTryGet(guildId, discordUserId);
            if (threadId == 0)
                return Results.Json(new ThreadMessagesResponse { Ok = true, GuildId = guildIdStr, ThreadId = "", Items = new List<ThreadMessageDto>() }, JsonWriteOpts);

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await EnsureThreadOpenAsync(chan);

            var msgs = await chan.GetMessagesAsync(limit: limit).FlattenAsync();

            var items = msgs
                .OrderBy(m => m.Timestamp)
                .Select(m =>
                {
                    var attachments = new List<string>();
                    try
                    {
                        foreach (var a in m.Attachments)
                            if (!string.IsNullOrWhiteSpace(a?.Url))
                                attachments.Add(a.Url);

                        foreach (var e in m.Embeds)
                            if (!string.IsNullOrWhiteSpace(e?.Url))
                                attachments.Add(e.Url);
                    }
                    catch { }

                    return new ThreadMessageDto
                    {
                        Id = m.Id.ToString(),
                        From = string.IsNullOrWhiteSpace(m.Author?.Username) ? "Dispatch" : m.Author!.Username,
                        Text = m.Content ?? "",
                        CreatedUtc = m.Timestamp.UtcDateTime,
                        Attachments = attachments
                    };
                })
                .ToList();

            var respObj = new ThreadMessagesResponse
            {
                Ok = true,
                GuildId = guildIdStr,
                ThreadId = threadId.ToString(),
                Items = items
            };

            return Results.Json(respObj, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Send thread message by user
        // POST /api/messages/thread/send/byuser?guildId=...
        // body: { discordUserId, text }
        // -----------------------------
        app.MapPost("/api/messages/thread/send/byuser", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            string raw;
            using (var reader = new StreamReader(req.Body))
                raw = await reader.ReadToEndAsync();

            string discordUserIdStr = "";
            string text = "";

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                discordUserIdStr = root.TryGetProperty("discordUserId", out var du) ? (du.GetString() ?? "") : "";
                text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
            }
            catch { }

            discordUserIdStr = (discordUserIdStr ?? "").Trim();
            text = (text ?? "").Trim();

            if (!ulong.TryParse(discordUserIdStr, out var discordUserId) || discordUserId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadDiscordUserId" }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "EmptyText" }, statusCode: 400);

            var threadId = ThreadStoreTryGet(guildId, discordUserId);
            if (threadId == 0)
            {
                var created = await EnsureDriverThreadAsync(guildId, discordUserId, "");
                if (created == 0)
                    return Results.Json(new { ok = false, error = "NoDispatchChannelOrThreadCreateFailed" }, statusCode: 500);
                threadId = created;
            }

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await EnsureThreadOpenAsync(chan);

            try
            {
                var sent = await chan.SendMessageAsync(text);
                return Results.Json(new { ok = true, threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "SendFailed", message = ex.Message }, statusCode: 500);
            }
        });

        // -----------------------------
        // ✅ Send thread message by user with files (multipart)
        // POST /api/messages/thread/sendform/byuser?guildId=...
        // form-data: discordUserId=... , text=... , files[]=...
        // -----------------------------
        app.MapPost("/api/messages/thread/sendform/byuser", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            if (!req.HasFormContentType)
                return Results.Json(new { ok = false, error = "ExpectedMultipartForm" }, statusCode: 415);

            var form = await req.ReadFormAsync();
            var discordUserIdStr = (form["discordUserId"].ToString() ?? "").Trim();
            var text = (form["text"].ToString() ?? "").Trim();

            if (!ulong.TryParse(discordUserIdStr, out var discordUserId) || discordUserId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadDiscordUserId" }, statusCode: 400);

            var threadId = ThreadStoreTryGet(guildId, discordUserId);
            if (threadId == 0)
            {
                var created = await EnsureDriverThreadAsync(guildId, discordUserId, "");
                if (created == 0)
                    return Results.Json(new { ok = false, error = "NoDispatchChannelOrThreadCreateFailed" }, statusCode: 500);
                threadId = created;
            }

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await EnsureThreadOpenAsync(chan);

            try
            {
                var files = new List<FileAttachment>();

                foreach (var f in form.Files)
                {
                    if (f == null || f.Length <= 0) continue;

                    if (f.Length > 24 * 1024 * 1024)
                        return Results.Json(new { ok = false, error = "FileTooLarge", file = f.FileName }, statusCode: 413);

                    var ms = new MemoryStream();
                    await f.CopyToAsync(ms);
                    ms.Position = 0;

                    files.Add(new FileAttachment(ms, f.FileName));
                }

                IUserMessage sent;

                if (files.Count > 0)
                {
                    sent = await chan.SendFilesAsync(files, text: text);

                    foreach (var fa in files)
                        try { fa.Stream.Dispose(); } catch { }
                }
                else
                {
                    sent = await chan.SendMessageAsync(text);
                }

                return Results.Json(new { ok = true, threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "SendFailed", message = ex.Message }, statusCode: 500);
            }
        });

        // -----------------------------
        // ✅ Mark read (adds ✅ reaction)
        // POST /api/messages/markread  { channelId, messageId }
        // POST /api/messages/markread/bulk  { channelId, messageIds[] }
        // -----------------------------
        app.MapPost("/api/messages/markread", async (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            MarkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<MarkReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                !ulong.TryParse(payload.MessageId, out var messageId))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            var m = await chan.GetMessageAsync(messageId);
            if (m == null) return Results.Json(new { ok = false, error = "MessageNotFound" }, statusCode: 404);

            await m.AddReactionAsync(new Emoji("✅"));
            return Results.Json(new { ok = true }, JsonWriteOpts);
        });

        app.MapPost("/api/messages/markread/bulk", async (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            MarkBulkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<MarkBulkReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                payload.MessageIds == null || payload.MessageIds.Count == 0)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            int okCount = 0;
            foreach (var idStr in payload.MessageIds)
            {
                if (!ulong.TryParse(idStr, out var mid)) continue;
                try
                {
                    var m = await chan.GetMessageAsync(mid);
                    if (m == null) continue;
                    await m.AddReactionAsync(new Emoji("✅"));
                    okCount++;
                }
                catch { }
            }

            return Results.Json(new { ok = true, marked = okCount }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Delete single/bulk
        // DELETE /api/messages/delete { channelId, messageId }
        // DELETE /api/messages/delete/bulk { channelId, messageIds[] }
        // -----------------------------
        app.MapDelete("/api/messages/delete", async (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            DeleteReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<DeleteReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                !ulong.TryParse(payload.MessageId, out var messageId))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            await chan.DeleteMessageAsync(messageId);
            return Results.Json(new { ok = true }, JsonWriteOpts);
        });

        app.MapDelete("/api/messages/delete/bulk", async (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            DeleteBulkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<DeleteBulkReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                payload.MessageIds == null || payload.MessageIds.Count == 0)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            int okCount = 0;
            foreach (var idStr in payload.MessageIds)
            {
                if (!ulong.TryParse(idStr, out var mid)) continue;
                try { await chan.DeleteMessageAsync(mid); okCount++; }
                catch { }
            }

            return Results.Json(new { ok = true, deleted = okCount }, JsonWriteOpts);
        });

        // Start HTTP server
        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");

        await Task.Delay(-1);
    }

    // -----------------------------
    // Discord command router
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (_client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;

        // ✅ Ignore ONLY our own bot messages (NOT webhooks, NOT other bots if they matter).
        try
        {
            if (_client.CurrentUser != null && msg.Author.Id == _client.CurrentUser.Id)
                return;
        }
        catch { }

        // ✅ Keep your existing thread-router integration FIRST (if it handles something, stop)
        if (_threadStore != null)
        {
            try
            {
                var handled = await LinkThreadCommand.TryHandleAsync(msg, _client, _threadStore);
                if (handled) return;
            }
            catch { /* do not block other commands */ }
        }

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        // Must be in a server for setup commands
        if (msg.Channel is not SocketGuildChannel guildChan)
        {
            if (content.Equals("!ping", StringComparison.OrdinalIgnoreCase))
                await msg.Channel.SendMessageAsync("pong ✅");
            else
                await msg.Channel.SendMessageAsync("This command must be used in a server channel (not DMs).");
            return;
        }

        var guild = guildChan.Guild;
        var guildIdStr = guild.Id.ToString();

        var gu = msg.Author as SocketGuildUser;
        var isAdmin = gu != null && (gu.GuildPermissions.Administrator || gu.GuildPermissions.ManageGuild);

        var body = content[1..].Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        var firstSpace = body.IndexOf(' ');
        var cmd = (firstSpace >= 0 ? body[..firstSpace] : body).Trim().ToLowerInvariant();
        var arg = (firstSpace >= 0 ? body[(firstSpace + 1)..] : "").Trim();

        if (cmd == "ping")
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (cmd == "help" || cmd == "commands")
        {
            await msg.Channel.SendMessageAsync(
                "Commands:\n" +
                "• !ping\n" +
                "• !setdispatchchannel #channel        (admin)\n" +
                "• !setupdispatch #channel             (admin, creates + saves dispatch webhook)\n" +
                "• !setdispatchwebhook <url>           (admin)\n" +
                "• !announcement #channel              (admin, creates + saves announcements webhook)\n" +
                "• !setannouncementwebhook <url>       (admin)\n" +
                "• !link / !linkthread                 (driver, creates/links dispatch thread)\n"
            );
            return;
        }

        if (cmd == "setdispatchchannel")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }
            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!setdispatchchannel #dispatch`"); return; }

            _dispatchStore?.SetDispatchChannel(guildIdStr, cid.Value);
            await msg.Channel.SendMessageAsync($"✅ Dispatch channel set to <#{cid.Value}>");
            return;
        }

        if (cmd == "setdispatchwebhook")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                await msg.Channel.SendMessageAsync("Usage: `!setdispatchwebhook https://discord.com/api/webhooks/...`");
                return;
            }

            _dispatchStore?.SetDispatchWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Dispatch webhook saved.");
            return;
        }

        if (cmd == "setupdispatch")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }

            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!setupdispatch #dispatch`"); return; }

            var ch = guild.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Channel not found (must be a text channel)."); return; }

            try
            {
                // ✅ FIX: no RestWebhook.Url. Build from Id + Token.
                var hook = await ch.CreateWebhookAsync("OverWatchELD Dispatch");
                var url = BuildWebhookUrl(hook);

                _dispatchStore?.SetDispatchChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync(
                        "✅ Dispatch channel set, webhook created.\n" +
                        "⚠️ Token wasn’t returned by this Discord.Net build.\n" +
                        "Copy the webhook URL from Discord and run: `!setdispatchwebhook <url>`"
                    );
                    return;
                }

                _dispatchStore?.SetDispatchWebhook(guildIdStr, url);
                await msg.Channel.SendMessageAsync($"✅ Dispatch configured.\nChannel: <#{ch.Id}>\nWebhook: saved.");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Failed to create webhook. Ensure the bot has **Manage Webhooks**.\n{ex.Message}");
            }

            return;
        }

        // ✅ USER REQUEST: “!Announcement to link webhook” (Discord announcements webhook)
        if (cmd == "announcement" || cmd == "announcements")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }

            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!announcement #announcements`"); return; }

            var ch = guild.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Channel not found (must be a text channel)."); return; }

            try
            {
                var hook = await ch.CreateWebhookAsync("OverWatchELD Announcements");
                var url = BuildWebhookUrl(hook);

                _dispatchStore?.SetAnnouncementChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync(
                        "✅ Announcement channel set, webhook created.\n" +
                        "⚠️ Token wasn’t returned by this Discord.Net build.\n" +
                        "Copy the webhook URL from Discord and run: `!setannouncementwebhook <url>`"
                    );
                    return;
                }

                _dispatchStore?.SetAnnouncementWebhook(guildIdStr, url);

                await msg.Channel.SendMessageAsync(
                    $"✅ Announcements linked.\n" +
                    $"Channel: <#{ch.Id}>\n" +
                    $"Webhook: saved.\n" +
                    $"ELD reads announcements from: `/api/vtc/announcements?guildId={guildIdStr}`"
                );
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Failed to create announcements webhook. Ensure the bot has **Manage Webhooks**.\n{ex.Message}");
            }

            return;
        }

        if (cmd == "setannouncementwebhook")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                await msg.Channel.SendMessageAsync("Usage: `!setannouncementwebhook https://discord.com/api/webhooks/...`");
                return;
            }

            _dispatchStore?.SetAnnouncementWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Announcement webhook saved.");
            return;
        }

        if (cmd == "link" || cmd == "linkthread")
        {
            if (_dispatchStore == null) { await msg.Channel.SendMessageAsync("❌ Dispatch store not ready."); return; }

            var settings = _dispatchStore.Get(guildIdStr);
            if (!ulong.TryParse(settings.DispatchChannelId, out var dispatchChannelId) || dispatchChannelId == 0)
            {
                await msg.Channel.SendMessageAsync("❌ Dispatch channel not set. Admin: `!setdispatchchannel #dispatch` or `!setupdispatch #dispatch`");
                return;
            }

            var dispatchChannel = guild.GetTextChannel(dispatchChannelId);
            if (dispatchChannel == null)
            {
                await msg.Channel.SendMessageAsync("❌ Dispatch channel saved, but it no longer exists.");
                return;
            }

            var userId = msg.Author.Id;
            var existing = ThreadStoreTryGet(guild.Id, userId);
            if (existing != 0)
            {
                await msg.Channel.SendMessageAsync($"✅ Already linked. Your dispatch thread: <#{existing}>");
                return;
            }

            try
            {
                var userLabel = string.IsNullOrWhiteSpace(msg.Author.Username) ? "driver" : msg.Author.Username;

                var starter = await dispatchChannel.SendMessageAsync($"📌 Dispatch thread created for **{userLabel}**.");
                var thread = await dispatchChannel.CreateThreadAsync(
                    name: $"dispatch-{SanitizeThreadName(userLabel)}",
                    autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                    type: ThreadType.PrivateThread,
                    invitable: false,
                    message: starter
                );

                try
                {
                    if (msg.Author is IGuildUser igu)
                        await thread.AddUserAsync(igu);
                }
                catch { }

                ThreadStoreSet(guild.Id, userId, thread.Id);

                await msg.Channel.SendMessageAsync($"✅ Linked! Your dispatch thread: <#{thread.Id}>");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Link failed: {ex.Message}");
            }

            return;
        }

        await msg.Channel.SendMessageAsync("❓ Unknown command. Type `!help`.");
    }

    // -----------------------------
    // Create driver thread automatically (used by ELD send if not linked)
    // -----------------------------
    private static async Task<ulong> EnsureDriverThreadAsync(ulong guildId, ulong discordUserId, string discordUsername)
    {
        try
        {
            if (_client == null || _dispatchStore == null) return 0;

            var g = _client.Guilds.FirstOrDefault(x => x.Id == guildId);
            if (g == null) return 0;

            var settings = _dispatchStore.Get(guildId.ToString());
            if (!ulong.TryParse(settings.DispatchChannelId, out var dispatchChannelId) || dispatchChannelId == 0)
                return 0;

            var dispatchChannel = g.GetTextChannel(dispatchChannelId);
            if (dispatchChannel == null) return 0;

            // If already mapped, return it.
            var existing = ThreadStoreTryGet(guildId, discordUserId);
            if (existing != 0) return existing;

            var label = string.IsNullOrWhiteSpace(discordUsername) ? "driver" : discordUsername;
            var starter = await dispatchChannel.SendMessageAsync($"📌 Dispatch thread created for **{label}**.");
            var thread = await dispatchChannel.CreateThreadAsync(
                name: $"dispatch-{SanitizeThreadName(label)}",
                autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                type: ThreadType.PrivateThread,
                invitable: false,
                message: starter
            );

            // Try to add user
            try
            {
                var user = g.GetUser(discordUserId);
                if (user != null)
                    await thread.AddUserAsync(user);
            }
            catch { }

            ThreadStoreSet(guildId, discordUserId, thread.Id);
            return thread.Id;
        }
        catch
        {
            return 0;
        }
    }

    // -----------------------------
    // FIX: Build webhook URL from RestWebhook.Id + RestWebhook.Token
    // -----------------------------
    private static string? BuildWebhookUrl(RestWebhook hook)
    {
        try
        {
            var token = (hook.Token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token)) return null;
            return $"https://discord.com/api/webhooks/{hook.Id}/{token}";
        }
        catch { return null; }
    }

    // -----------------------------
    // Thread map helpers (reflection + fallback)
    // -----------------------------
    private static ulong ThreadStoreTryGet(ulong guildId, ulong userId)
    {
        try
        {
            if (_threadStore != null)
            {
                var t = _threadStore.GetType();

                foreach (var name in new[] { "TryGetThreadId", "GetThreadId", "TryGet", "Get" })
                {
                    var mi = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null, types: new[] { typeof(ulong), typeof(ulong) }, modifiers: null);
                    if (mi != null)
                    {
                        var val = mi.Invoke(_threadStore, new object[] { guildId, userId });
                        if (val is ulong u && u != 0) return u;
                        if (val is long l && l != 0) return (ulong)l;
                        if (val is string s && ulong.TryParse(s, out var us) && us != 0) return us;
                    }
                }
            }
        }
        catch { }

        try { return _threadFallback?.TryGet(guildId, userId) ?? 0; }
        catch { return 0; }
    }

    private static void ThreadStoreSet(ulong guildId, ulong userId, ulong threadId)
    {
        try
        {
            if (_threadStore != null)
            {
                var t = _threadStore.GetType();

                foreach (var name in new[] { "SetThreadId", "Set", "Put", "Upsert" })
                {
                    var mi = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null, types: new[] { typeof(ulong), typeof(ulong), typeof(ulong) }, modifiers: null);
                    if (mi != null)
                    {
                        mi.Invoke(_threadStore, new object[] { guildId, userId, threadId });
                        // also mirror to fallback (harmless) so we never lose mapping
                        try { _threadFallback?.Set(guildId, userId, threadId); } catch { }
                        return;
                    }
                }
            }
        }
        catch { }

        try { _threadFallback?.Set(guildId, userId, threadId); } catch { }
    }

    private static string SanitizeThreadName(string s)
    {
        s = (s ?? "driver").Trim().ToLowerInvariant();
        if (s.Length > 32) s = s.Substring(0, 32);
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "driver";
        return safe;
    }

    private static ulong? TryParseChannelIdFromMention(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        // formats: <#123>, 123
        if (raw.StartsWith("<#") && raw.EndsWith(">"))
            raw = raw.Substring(2, raw.Length - 3);

        return ulong.TryParse(raw, out var id) ? id : null;
    }

    // -----------------------------
    // Channel resolution (Railway-safe)
    // -----------------------------
    private static async Task<IMessageChannel?> ResolveChannelAsync(ulong channelId)
    {
        if (_client == null) return null;

        if (_client.GetChannel(channelId) is IMessageChannel cached)
            return cached;

        var rest = await _client.Rest.GetChannelAsync(channelId);

        if (rest is RestThreadChannel rt) return rt;
        if (rest is RestTextChannel rtxt) return rtxt;

        return rest as IMessageChannel;
    }

    private static async Task EnsureThreadOpenAsync(IMessageChannel chan)
    {
        if (chan is SocketThreadChannel st && st.IsArchived)
            await st.ModifyAsync(p => p.Archived = false);

        if (chan is RestThreadChannel rt && rt.IsArchived)
            await rt.ModifyAsync(p => p.Archived = false);
    }
}
