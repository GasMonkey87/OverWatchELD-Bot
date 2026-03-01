// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Two-way chat: Discord Thread ↔ ELD (fetch/send/reactions/delete)
// ✅ Railway-safe: REST fallback when channel/thread not in socket cache
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ ELD Compatibility endpoints to prevent 404s (/api/vtc/name, /api/vtc/status, /api/servers, etc.)
// ✅ Admin setup commands:
//    - !setdispatchchannel #channel
//    - !setdispatchwebhook <url>
//    - !setupdispatch #channel (creates webhook)
//    - !announcement #channel  (sets announcement channel)
// ✅ Announcement API for ELD (pollable):
//    - GET /api/announcements?guildId=...&limit=25
//    - plus aliases: /api/messages/announcements, /api/vtc/announcements, /api/eld/announcements
// ✅ Keeps your existing thread-router integration: ThreadMapStore + DiscordThreadRouter + LinkThreadCommand
// ✅ Adds !link / !linkthread that works even if ThreadMapStore API differs (reflection + fallback store)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    // HTTP payload models
    // -----------------------------
    private sealed class ThreadSendReq
    {
        public string? Text { get; set; }
        public List<string>? AttachmentUrls { get; set; }
    }

    private sealed class ThreadMsgsRespItem
    {
        public string Id { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string From { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public List<string> Attachments { get; set; } = new(); // URLs
    }

    private sealed class AnnouncementRespItem
    {
        public string Id { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string From { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public List<string> Attachments { get; set; } = new();
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

    // -----------------------------
    // Settings store (guild-scoped)
    // -----------------------------
    private sealed class DispatchSettings
    {
        public string GuildId { get; set; } = "";
        public string? DispatchChannelId { get; set; }    // text channel id
        public string? DispatchWebhookUrl { get; set; }   // optional
        public string? AnnouncementChannelId { get; set; } // text channel id
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
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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

        // Init stores
        _threadStore = new ThreadMapStore(ThreadMapPath);
        _threadFallback = new ThreadMapFallback(ThreadMapFallbackPath);
        _dispatchStore = new DispatchSettingsStore(DispatchCfgPath);

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
        // Servers list (prevents empty list at cold start)
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

        // -----------------------------
        // ✅ Compatibility endpoints (prevents 404s from older ELD builds)
        // -----------------------------
        object GetStatusPayload()
        {
            var client = _client;
            return new
            {
                ok = client != null && _discordReady,
                discordReady = _discordReady,
                guildCount = client?.Guilds.Count ?? 0
            };
        }

        object GetNamePayload(string? guildIdStr)
        {
            var client = _client;
            if (client == null) return new { ok = false, error = "DiscordNotReady" };

            SocketGuild? g = null;

            if (!string.IsNullOrWhiteSpace(guildIdStr) && ulong.TryParse(guildIdStr, out var gid))
                g = client.Guilds.FirstOrDefault(x => x.Id == gid);

            // fallback: first guild (for older single-server ELD builds)
            g ??= client.Guilds.FirstOrDefault();

            return new
            {
                ok = g != null,
                guildId = g?.Id.ToString() ?? "",
                name = g?.Name ?? "Not Connected",
                vtcName = g?.Name ?? "Not Connected"
            };
        }

        app.MapGet("/api/vtc/status", () => Results.Json(GetStatusPayload(), JsonWriteOpts));
        app.MapGet("/api/status", () => Results.Json(GetStatusPayload(), JsonWriteOpts));
        app.MapGet("/api/discord/status", () => Results.Json(GetStatusPayload(), JsonWriteOpts));

        app.MapGet("/api/vtc/name", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            return Results.Json(GetNamePayload(guildId), JsonWriteOpts);
        });

        app.MapGet("/api/vtc", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            return Results.Json(GetNamePayload(guildId), JsonWriteOpts);
        });

        app.MapGet("/api/servers", () => Results.Redirect("/api/vtc/servers", permanent: false));
        app.MapGet("/api/discord/servers", () => Results.Redirect("/api/vtc/servers", permanent: false));
        app.MapGet("/api/vtc/guilds", () => Results.Redirect("/api/vtc/servers", permanent: false));

        // -----------------------------
        // ✅ Announcements for ELD (pollable)
        // GET /api/announcements?guildId=...&limit=25
        // Aliases: /api/messages/announcements, /api/vtc/announcements, /api/eld/announcements
        // -----------------------------
        async Task<IResult> GetAnnouncements(HttpRequest req)
        {
            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var limitStr = (req.Query["limit"].ToString() ?? "25").Trim();
            if (!int.TryParse(limitStr, out var limit)) limit = 25;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            if (string.IsNullOrWhiteSpace(guildIdStr))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(guildIdStr, out var guildId) || guildId == 0)
                return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

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
            var items = msgs
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

                    return new AnnouncementRespItem
                    {
                        Id = m.Id.ToString(),
                        ChannelId = annChannelId.ToString(),
                        From = string.IsNullOrWhiteSpace(m.Author?.Username) ? "Announcements" : m.Author!.Username,
                        Text = m.Content ?? "",
                        CreatedUtc = m.Timestamp.UtcDateTime,
                        Attachments = attachments
                    };
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId = guildIdStr, channelId = annChannelId.ToString(), items }, JsonWriteOpts);
        }

        app.MapGet("/api/announcements", GetAnnouncements);
        app.MapGet("/api/messages/announcements", GetAnnouncements);
        app.MapGet("/api/vtc/announcements", GetAnnouncements);
        app.MapGet("/api/eld/announcements", GetAnnouncements);

        // -----------------------------
        // ✅ Fetch thread messages (+ attachment URLs)
        // GET /api/messages/thread?guildId=...&threadId=...&limit=50
        // -----------------------------
        app.MapGet("/api/messages/thread", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var threadIdStr = (req.Query["threadId"].ToString() ?? "").Trim();
            var limitStr = (req.Query["limit"].ToString() ?? "50").Trim();
            if (!int.TryParse(limitStr, out var limit)) limit = 50;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(threadIdStr, out var threadId) || threadId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadThreadId" }, statusCode: 400);

            if (_client == null)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

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

                    return new ThreadMsgsRespItem
                    {
                        Id = m.Id.ToString(),
                        ChannelId = threadIdStr,
                        From = string.IsNullOrWhiteSpace(m.Author?.Username) ? "Dispatch" : m.Author!.Username,
                        Text = m.Content ?? "",
                        CreatedUtc = m.Timestamp.UtcDateTime,
                        Attachments = attachments
                    };
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId, threadId = threadIdStr, items }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Send to thread (JSON) (text + optional attachment URLs)
        // POST /api/messages/thread/send?guildId=...&threadId=...
        // body: { "text":"...", "attachmentUrls":[...] }
        // -----------------------------
        app.MapPost("/api/messages/thread/send", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var threadIdStr = (req.Query["threadId"].ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(threadIdStr, out var threadId) || threadId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadThreadId" }, statusCode: 400);

            if (_client == null)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            string rawBody;
            using (var reader = new StreamReader(req.Body))
                rawBody = await reader.ReadToEndAsync();

            ThreadSendReq? payload;
            try { payload = JsonSerializer.Deserialize<ThreadSendReq>(rawBody, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Text))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await EnsureThreadOpenAsync(chan);

            var msgText = payload.Text.Trim();
            var urls = (payload.AttachmentUrls ?? new List<string>())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u.Trim())
                .ToList();

            if (urls.Count > 0)
                msgText += "\n" + string.Join("\n", urls);

            try
            {
                var sent = await chan.SendMessageAsync(msgText);
                return Results.Json(new { ok = true, messageId = sent.Id.ToString(), channelId = threadIdStr }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[thread/send] ❌ {ex}");
                return Results.Json(new { ok = false, error = "SendFailed", message = ex.Message }, statusCode: 500);
            }
        });

        // -----------------------------
        // ✅ Send to thread (MULTIPART) (text + upload files + optional attachmentUrls)
        // POST /api/messages/thread/sendform?guildId=...&threadId=...
        // form-data: text=... , files[]=..., attachmentUrls[]=...
        // -----------------------------
        app.MapPost("/api/messages/thread/sendform", async (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var threadIdStr = (req.Query["threadId"].ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!ulong.TryParse(threadIdStr, out var threadId) || threadId == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadThreadId" }, statusCode: 400);

            if (_client == null)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            if (!req.HasFormContentType)
                return Results.Json(new { ok = false, error = "ExpectedMultipartForm" }, statusCode: 415);

            var form = await req.ReadFormAsync();
            var text = (form["text"].ToString() ?? "").Trim();

            var urlList = form["attachmentUrls"].ToArray()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

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

                    // Optional size guard (Discord limits vary). Adjust if needed.
                    if (f.Length > 24 * 1024 * 1024)
                        return Results.Json(new { ok = false, error = "FileTooLarge", file = f.FileName }, statusCode: 413);

                    var ms = new MemoryStream();
                    await f.CopyToAsync(ms);
                    ms.Position = 0;

                    files.Add(new FileAttachment(ms, f.FileName));
                }

                var finalText = text;
                if (urlList.Count > 0)
                    finalText = (string.IsNullOrWhiteSpace(finalText) ? "" : finalText + "\n") + string.Join("\n", urlList);

                IUserMessage? sent;

                if (files.Count > 0)
                {
                    sent = await chan.SendFilesAsync(files, text: finalText);

                    foreach (var fa in files)
                        try { fa.Stream.Dispose(); } catch { }
                }
                else
                {
                    sent = await chan.SendMessageAsync(finalText);
                }

                return Results.Json(new { ok = true, messageId = sent.Id.ToString(), channelId = threadIdStr }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[thread/sendform] ❌ {ex}");
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

            var msg = await chan.GetMessageAsync(messageId);
            if (msg == null) return Results.Json(new { ok = false, error = "MessageNotFound" }, statusCode: 404);

            await msg.AddReactionAsync(new Emoji("✅"));
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
                    var msg = await chan.GetMessageAsync(mid);
                    if (msg == null) continue;
                    await msg.AddReactionAsync(new Emoji("✅"));
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
        if (msg.Author.IsBot) return;

        // ✅ Keep your existing thread link behavior FIRST (if it handles something, stop)
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

        // Must be in a server for admin setup commands
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
                "• !setdispatchchannel #channel   (admin)\n" +
                "• !setdispatchwebhook <url>      (admin)\n" +
                "• !setupdispatch #channel        (admin, creates webhook)\n" +
                "• !announcement #channel         (admin, sets announcements channel)\n" +
                "• !link / !linkthread            (driver links dispatch thread)\n"
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

            _dispatchStore?.SetDispatchWebhook(guildIdStr, arg);
            await msg.Channel.SendMessageAsync("✅ Dispatch webhook saved.");
            return;
        }

        if (cmd == "setupdispatch" || cmd == "setupdispatchwebhook")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }

            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!setupdispatch #dispatch`"); return; }

            var ch = guild.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Channel not found (must be a text channel)."); return; }

            try
            {
                // Requires Manage Webhooks permission for the bot in that channel
                var hook = await ch.CreateWebhookAsync("OverWatchELD Dispatch");
                _dispatchStore?.SetDispatchChannel(guildIdStr, ch.Id);
                _dispatchStore?.SetDispatchWebhook(guildIdStr, hook.Url);

                await msg.Channel.SendMessageAsync($"✅ Dispatch configured.\nChannel: <#{ch.Id}>\nWebhook: saved.");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Failed to create webhook. Ensure the bot has **Manage Webhooks**.\n{ex.Message}");
            }

            return;
        }

        if (cmd == "announcement" || cmd == "announcements")
        {
            if (!isAdmin) { await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server / Admin required)."); return; }

            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!announcement #announcements`"); return; }

            _dispatchStore?.SetAnnouncementChannel(guildIdStr, cid.Value);
            await msg.Channel.SendMessageAsync($"✅ Announcement channel set to <#{cid.Value}>.\nELD will pull announcements via `/api/announcements?guildId={guildIdStr}`");
            return;
        }

        if (cmd == "link" || cmd == "linkthread")
        {
            if (_dispatchStore == null) { await msg.Channel.SendMessageAsync("❌ Dispatch store not ready."); return; }
            if (_threadFallback == null) { await msg.Channel.SendMessageAsync("❌ Thread store not ready."); return; }

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
                catch { /* if missing perms to add user, ignore */ }

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
    // Thread map helpers (reflection + fallback)
    // -----------------------------
    private static ulong ThreadStoreTryGet(ulong guildId, ulong userId)
    {
        // 1) Try ThreadMapStore methods if present (different builds name these differently)
        try
        {
            if (_threadStore != null)
            {
                var t = _threadStore.GetType();

                // Common method name patterns:
                // TryGetThreadId(ulong guildId, ulong userId) -> ulong
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

        // 2) Fallback store
        try
        {
            return _threadFallback?.TryGet(guildId, userId) ?? 0;
        }
        catch { return 0; }
    }

    private static void ThreadStoreSet(ulong guildId, ulong userId, ulong threadId)
    {
        // 1) Try ThreadMapStore methods if present
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
                        return;
                    }
                }
            }
        }
        catch { }

        // 2) Fallback store always updated
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

        // REST fallback
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
