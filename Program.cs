// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ WORKS WITH YOUR CURRENT ELD ZIP (BotApiService.cs)
// ✅ ELD endpoints implemented:
//    - GET  /api/messages
//    - POST /api/messages/send
//    - GET  /api/messages/thread/byuser
//    - POST /api/messages/thread/send/byuser
//    - POST /api/messages/thread/sendform/byuser
//    - POST /api/messages/markread/bulk
//    - DELETE /api/messages/delete/bulk
// ✅ FIX: If ELD is NOT linked (discordUserId missing), bot FALLS BACK to dispatch channel/webhook.
// ✅ Discord commands:
//    - !setupdispatch #channel        (creates + saves dispatch webhook)
//    - !setdispatchchannel #channel
//    - !setdispatchwebhook <url>
//    - !announcement #channel         (creates + saves announcements webhook)  <-- requested
//    - !setannouncementwebhook <url>
//    - !ping, !help
// ✅ Announcements endpoints:
//    - GET  /api/vtc/announcements?guildId=...&limit=25
//    - POST /api/vtc/announcements/post  (optional: ELD -> Discord announcements via webhook)
//
// IMPORTANT:
// - Webhook-authored messages are NOT ignored (we ignore only our own bot user id).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static volatile bool _discordReady = false;

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    private static ThreadMapStore? _threadStore;
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

    private static readonly string DispatchCfgPath = Path.Combine(DataDir, "dispatch_settings.json");
    private static DispatchSettingsStore? _dispatchStore;

    private sealed class DispatchSettings
    {
        public string GuildId { get; set; } = "";
        public string? DispatchChannelId { get; set; }
        public string? DispatchWebhookUrl { get; set; }
        public string? AnnouncementChannelId { get; set; }
        public string? AnnouncementWebhookUrl { get; set; }
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
                File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, JsonWriteOpts));
            }
            catch { }
        }
    }

    // -----------------------------
    // Models matching ELD payloads
    // -----------------------------
    private sealed class SendMessageReq
    {
        [JsonPropertyName("driverName")]
        public string? DriverName { get; set; }  // ELD sends "driverName"

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
        [JsonPropertyName("driverName")]
        public string DriverName { get; set; } = "";
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class MarkBulkReq
    {
        public string? ChannelId { get; set; }
        public List<string>? MessageIds { get; set; }
    }

    private sealed class DeleteBulkReq
    {
        public string? ChannelId { get; set; }
        public List<string>? MessageIds { get; set; }
    }

    private sealed class AnnouncementPostReq
    {
        public string? GuildId { get; set; }
        public string? Text { get; set; }
        public string? Author { get; set; }
    }

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);

        _threadStore = new ThreadMapStore(ThreadMapPath);
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
            Console.WriteLine("✅ Discord READY");
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

        var portStr = (Environment.GetEnvironmentVariable("PORT") ?? "8080").Trim();
        if (!int.TryParse(portStr, out var port)) port = 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/vtc/servers", () =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var servers = _client.Guilds.Select(g => new { guildId = g.Id.ToString(), name = g.Name }).ToArray();
            return Results.Json(new { ok = true, servers }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Announcements -> ELD (poll)
        // -----------------------------
        app.MapGet("/api/vtc/announcements", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            var limitStr = (req.Query["limit"].ToString() ?? "25").Trim();
            if (!int.TryParse(limitStr, out var limit)) limit = 25;
            limit = Math.Clamp(limit, 1, 100);

            var guild = _client.Guilds.FirstOrDefault(g => g.Id == gid);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guildIdStr);
            if (settings == null || !ulong.TryParse(settings.AnnouncementChannelId, out var annChId) || annChId == 0)
                return Results.Json(new { ok = false, error = "AnnouncementChannelNotSet" }, statusCode: 400);

            var ch = guild.GetTextChannel(annChId);
            if (ch == null) return Results.Json(new { ok = false, error = "AnnouncementChannelNotFound" }, statusCode: 404);

            var msgs = await ch.GetMessagesAsync(limit: limit).FlattenAsync();
            var announcements = msgs.OrderByDescending(m => m.Timestamp).Select(m =>
            {
                var atts = new List<string>();
                try
                {
                    foreach (var a in m.Attachments) if (!string.IsNullOrWhiteSpace(a?.Url)) atts.Add(a.Url);
                    foreach (var e in m.Embeds) if (!string.IsNullOrWhiteSpace(e?.Url)) atts.Add(e.Url);
                }
                catch { }

                return new
                {
                    text = (m.Content ?? "").Trim(),
                    author = (m.Author?.Username ?? "Announcements").Trim(),
                    createdUtc = m.Timestamp.UtcDateTime,
                    attachments = atts
                };
            }).ToArray();

            return Results.Json(new { ok = true, guildId = guildIdStr, announcements }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Announcements webhook post (optional)
        // -----------------------------
        app.MapPost("/api/vtc/announcements/post", async (HttpRequest req) =>
        {
            AnnouncementPostReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<AnnouncementPostReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            var guildIdStr = (payload?.GuildId ?? "").Trim();
            var text = (payload?.Text ?? "").Trim();
            var author = (payload?.Author ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildIdStr)) return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(text)) return Results.Json(new { ok = false, error = "EmptyText" }, statusCode: 400);

            var settings = _dispatchStore?.Get(guildIdStr);
            var hookUrl = (settings?.AnnouncementWebhookUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(hookUrl)) return Results.Json(new { ok = false, error = "AnnouncementWebhookNotSet" }, statusCode: 400);

            var content = string.IsNullOrWhiteSpace(author) ? text : $"**{author}:** {text}";
            var json = JsonSerializer.Serialize(new { username = "OverWatch ELD", content }, JsonWriteOpts);

            using var resp = await _http.PostAsync(hookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
            var respText = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return Results.Json(new { ok = false, error = "WebhookSendFailed", status = (int)resp.StatusCode, body = respText }, statusCode: 502);

            return Results.Json(new { ok = true }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ GET /api/messages (legacy)
        // FALLBACK: return dispatch channel messages (works even if not linked)
        // -----------------------------
        app.MapGet("/api/messages", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
            {
                // older builds: use first guild
                gid = _client.Guilds.FirstOrDefault()?.Id ?? 0;
                guildIdStr = gid.ToString();
            }

            if (gid == 0) return Results.Json(new { ok = false, error = "NoGuild" }, statusCode: 500);

            var guild = _client.Guilds.FirstOrDefault(g => g.Id == gid);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guildIdStr);
            if (settings == null || !ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return Results.Json(new MessagesResponse { Ok = true, GuildId = guildIdStr, Items = new List<MessageDto>() }, JsonWriteOpts);

            var ch = guild.GetTextChannel(dispatchChId);
            if (ch == null)
                return Results.Json(new MessagesResponse { Ok = true, GuildId = guildIdStr, Items = new List<MessageDto>() }, JsonWriteOpts);

            var msgs = await ch.GetMessagesAsync(limit: 25).FlattenAsync();
            var items = msgs.OrderByDescending(m => m.Timestamp).Select(m => new MessageDto
            {
                Id = m.Id.ToString(),
                GuildId = guildIdStr,
                DriverName = m.Author?.Username ?? "Dispatch",
                Text = m.Content ?? "",
                Source = "discord",
                CreatedUtc = m.Timestamp.UtcDateTime
            }).ToList();

            return Results.Json(new MessagesResponse { Ok = true, GuildId = guildIdStr, Items = items }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ POST /api/messages/send (ELD -> Discord)
        // If discordUserId missing => FALLBACK to dispatch channel/webhook so it STILL WORKS.
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
            var driverName = (payload.DriverName ?? "").Trim();
            var discordUserIdStr = (payload.DiscordUserId ?? "").Trim();
            var discordUsername = (payload.DiscordUsername ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildIdStr))
            {
                var g0 = _client.Guilds.FirstOrDefault();
                guildIdStr = g0?.Id.ToString() ?? "";
            }

            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            var who = !string.IsNullOrWhiteSpace(driverName) ? driverName :
                      !string.IsNullOrWhiteSpace(discordUsername) ? discordUsername :
                      "Driver";

            // ✅ If linked, send to thread-per-driver
            if (ulong.TryParse(discordUserIdStr, out var duid) && duid != 0)
            {
                var threadId = ThreadStoreTryGet(gid, duid);
                if (threadId == 0)
                {
                    var created = await EnsureDriverThreadAsync(gid, duid, who);
                    if (created == 0)
                        return Results.Json(new { ok = false, error = "ThreadCreateFailedOrDispatchNotSet" }, statusCode: 500);
                    threadId = created;
                }

                var chan = await ResolveChannelAsync(threadId);
                if (chan == null) return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);
                await EnsureThreadOpenAsync(chan);

                var sent = await chan.SendMessageAsync($"**{who}:** {text}");
                Console.WriteLine($"[SEND->THREAD] guild={gid} user={duid} thread={threadId} msg={sent.Id}");
                return Results.Json(new { ok = true, threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
            }

            // ✅ FALLBACK: NOT LINKED => send to dispatch channel/webhook
            var settings = _dispatchStore?.Get(guildIdStr);
            var hookUrl = (settings?.DispatchWebhookUrl ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(hookUrl))
            {
                var json = JsonSerializer.Serialize(new
                {
                    username = who,
                    content = text
                }, JsonWriteOpts);

                using var resp = await _http.PostAsync(hookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                Console.WriteLine($"[SEND->DISPATCH_WEBHOOK] guild={gid} status={(int)resp.StatusCode}");
                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { ok = false, error = "DispatchWebhookSendFailed", status = (int)resp.StatusCode, body }, statusCode: 502);

                return Results.Json(new { ok = true, mode = "dispatchWebhook" }, JsonWriteOpts);
            }

            // channel fallback
            var guild = _client.Guilds.FirstOrDefault(g => g.Id == gid);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            if (settings == null || !ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return Results.Json(new { ok = false, error = "DispatchNotConfigured" }, statusCode: 400);

            var ch2 = guild.GetTextChannel(dispatchChId);
            if (ch2 == null) return Results.Json(new { ok = false, error = "DispatchChannelNotFound" }, statusCode: 404);

            var sent2 = await ch2.SendMessageAsync($"**{who}:** {text}");
            Console.WriteLine($"[SEND->DISPATCH_CHANNEL] guild={gid} ch={dispatchChId} msg={sent2.Id}");
            return Results.Json(new { ok = true, mode = "dispatchChannel", messageId = sent2.Id.ToString() }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ GET /api/messages/thread/byuser (ELD poll)
        // -----------------------------
        app.MapGet("/api/messages/thread/byuser", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var discordUserIdStr = (req.Query["discordUserId"].ToString() ?? "").Trim();

            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            if (!ulong.TryParse(discordUserIdStr, out var duid) || duid == 0)
            {
                // Not linked => return empty but OK (prevents “dead” UX)
                return Results.Json(new ThreadMessagesResponse { Ok = true, GuildId = guildIdStr, ThreadId = "", Items = new List<ThreadMessageDto>() }, JsonWriteOpts);
            }

            var threadId = ThreadStoreTryGet(gid, duid);
            if (threadId == 0)
                return Results.Json(new ThreadMessagesResponse { Ok = true, GuildId = guildIdStr, ThreadId = "", Items = new List<ThreadMessageDto>() }, JsonWriteOpts);

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await EnsureThreadOpenAsync(chan);

            var msgs = await chan.GetMessagesAsync(limit: 75).FlattenAsync();
            var items = msgs.OrderBy(m => m.Timestamp).Select(m =>
            {
                var atts = new List<string>();
                try
                {
                    foreach (var a in m.Attachments) if (!string.IsNullOrWhiteSpace(a?.Url)) atts.Add(a.Url);
                    foreach (var e in m.Embeds) if (!string.IsNullOrWhiteSpace(e?.Url)) atts.Add(e.Url);
                }
                catch { }

                return new ThreadMessageDto
                {
                    Id = m.Id.ToString(),
                    From = m.Author?.Username ?? "Dispatch",
                    Text = m.Content ?? "",
                    CreatedUtc = m.Timestamp.UtcDateTime,
                    Attachments = atts
                };
            }).ToList();

            return Results.Json(new ThreadMessagesResponse { Ok = true, GuildId = guildIdStr, ThreadId = threadId.ToString(), Items = items }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ POST /api/messages/thread/send/byuser (ELD -> thread)
        // -----------------------------
        app.MapPost("/api/messages/thread/send/byuser", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            string raw;
            using (var r = new StreamReader(req.Body)) raw = await r.ReadToEndAsync();

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

            if (!ulong.TryParse(discordUserIdStr, out var duid) || duid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadDiscordUserId" }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "EmptyText" }, statusCode: 400);

            var threadId = ThreadStoreTryGet(gid, duid);
            if (threadId == 0)
            {
                var created = await EnsureDriverThreadAsync(gid, duid, "Driver");
                if (created == 0) return Results.Json(new { ok = false, error = "ThreadCreateFailedOrDispatchNotSet" }, statusCode: 500);
                threadId = created;
            }

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null) return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);
            await EnsureThreadOpenAsync(chan);

            var sent = await chan.SendMessageAsync(text.Trim());
            return Results.Json(new { ok = true, threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ sendform/byuser (files)
        // -----------------------------
        app.MapPost("/api/messages/thread/sendform/byuser", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

            if (!req.HasFormContentType)
                return Results.Json(new { ok = false, error = "ExpectedMultipartForm" }, statusCode: 415);

            var form = await req.ReadFormAsync();
            var discordUserIdStr = (form["discordUserId"].ToString() ?? "").Trim();
            var text = (form["text"].ToString() ?? "").Trim();

            if (!ulong.TryParse(discordUserIdStr, out var duid) || duid == 0)
                return Results.Json(new { ok = false, error = "MissingOrBadDiscordUserId" }, statusCode: 400);

            var threadId = ThreadStoreTryGet(gid, duid);
            if (threadId == 0)
            {
                var created = await EnsureDriverThreadAsync(gid, duid, "Driver");
                if (created == 0) return Results.Json(new { ok = false, error = "ThreadCreateFailedOrDispatchNotSet" }, statusCode: 500);
                threadId = created;
            }

            var chan = await ResolveChannelAsync(threadId);
            if (chan == null) return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);
            await EnsureThreadOpenAsync(chan);

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
                foreach (var fa in files) try { fa.Stream.Dispose(); } catch { }
            }
            else
            {
                sent = await chan.SendMessageAsync(text);
            }

            return Results.Json(new { ok = true, threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
        });

        // -----------------------------
        // markread/bulk + delete/bulk
        // -----------------------------
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
                try { await chan.DeleteMessageAsync(mid); okCount++; } catch { }
            }

            return Results.Json(new { ok = true, deleted = okCount }, JsonWriteOpts);
        });

        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        await Task.Delay(-1);
    }

    // -----------------------------
    // Discord commands (admin setup)
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (_client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;

        // Ignore only our own bot
        try { if (_client.CurrentUser != null && msg.Author.Id == _client.CurrentUser.Id) return; } catch { }

        // Keep your existing thread-router integration (if it handles something)
        if (_threadStore != null)
        {
            try
            {
                var handled = await LinkThreadCommand.TryHandleAsync(msg, _client, _threadStore);
                if (handled) return;
            }
            catch { }
        }

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        if (msg.Channel is not SocketGuildChannel guildChan)
        {
            if (content.Equals("!ping", StringComparison.OrdinalIgnoreCase))
                await msg.Channel.SendMessageAsync("pong ✅");
            else
                await msg.Channel.SendMessageAsync("Use setup commands in a server channel.");
            return;
        }

        var guild = guildChan.Guild;
        var guildIdStr = guild.Id.ToString();

        var gu = msg.Author as SocketGuildUser;
        var isAdmin = gu != null && (gu.GuildPermissions.Administrator || gu.GuildPermissions.ManageGuild);

        var body = content[1..].Trim();
        var parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = (parts.Length > 0 ? parts[0] : "").Trim().ToLowerInvariant();
        var arg = (parts.Length > 1 ? parts[1] : "").Trim();

        if (cmd == "ping")
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (cmd == "help")
        {
            await msg.Channel.SendMessageAsync(
                "Commands:\n" +
                "• !setupdispatch #channel (admin)\n" +
                "• !setdispatchchannel #channel (admin)\n" +
                "• !setdispatchwebhook <url> (admin)\n" +
                "• !announcement #channel (admin)  ✅ links announcements webhook\n" +
                "• !setannouncementwebhook <url> (admin)\n"
            );
            return;
        }

        if (!isAdmin)
        {
            await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server/Admin required).");
            return;
        }

        if (cmd == "setdispatchchannel")
        {
            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!setdispatchchannel #dispatch`"); return; }
            _dispatchStore?.SetDispatchChannel(guildIdStr, cid.Value);
            await msg.Channel.SendMessageAsync($"✅ Dispatch channel set to <#{cid.Value}>");
            return;
        }

        if (cmd == "setdispatchwebhook")
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await msg.Channel.SendMessageAsync("Usage: `!setdispatchwebhook https://discord.com/api/webhooks/...`"); return; }
            _dispatchStore?.SetDispatchWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Dispatch webhook saved.");
            return;
        }

        if (cmd == "setupdispatch")
        {
            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!setupdispatch #dispatch`"); return; }

            var ch = guild.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Must be a text channel."); return; }

            try
            {
                var hook = await ch.CreateWebhookAsync("OverWatchELD Dispatch");
                var url = BuildWebhookUrl(hook);

                _dispatchStore?.SetDispatchChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync(
                        "✅ Dispatch channel set. Webhook created but token wasn’t returned.\n" +
                        "Copy webhook URL from Discord and run `!setdispatchwebhook <url>`"
                    );
                    return;
                }

                _dispatchStore?.SetDispatchWebhook(guildIdStr, url);
                await msg.Channel.SendMessageAsync($"✅ Dispatch configured.\nChannel: <#{ch.Id}>\nWebhook: saved.");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Webhook create failed. Bot needs **Manage Webhooks**.\n{ex.Message}");
            }
            return;
        }

        // ✅ Your request: !Announcement to link webhook
        if (cmd == "announcement" || cmd == "announcements")
        {
            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!announcement #announcements`"); return; }

            var ch = guild.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Must be a text channel."); return; }

            try
            {
                var hook = await ch.CreateWebhookAsync("OverWatchELD Announcements");
                var url = BuildWebhookUrl(hook);

                _dispatchStore?.SetAnnouncementChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync(
                        "✅ Announcement channel set. Webhook created but token wasn’t returned.\n" +
                        "Copy webhook URL from Discord and run `!setannouncementwebhook <url>`"
                    );
                    return;
                }

                _dispatchStore?.SetAnnouncementWebhook(guildIdStr, url);

                await msg.Channel.SendMessageAsync(
                    $"✅ Announcements linked.\nChannel: <#{ch.Id}>\nWebhook: saved.\n" +
                    $"ELD reads from: `/api/vtc/announcements?guildId={guildIdStr}`"
                );
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Announcements webhook failed. Bot needs **Manage Webhooks**.\n{ex.Message}");
            }
            return;
        }

        if (cmd == "setannouncementwebhook")
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await msg.Channel.SendMessageAsync("Usage: `!setannouncementwebhook https://discord.com/api/webhooks/...`"); return; }
            _dispatchStore?.SetAnnouncementWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Announcement webhook saved.");
            return;
        }

        await msg.Channel.SendMessageAsync("❓ Unknown. Type `!help`.");
    }

    // -----------------------------
    // Thread mapping helpers
    // -----------------------------
    private static ulong ThreadStoreTryGet(ulong guildId, ulong userId)
    {
        try
        {
            if (_threadStore == null) return 0;
            var t = _threadStore.GetType();
            foreach (var name in new[] { "TryGetThreadId", "GetThreadId", "TryGet", "Get" })
            {
                var mi = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(ulong), typeof(ulong) }, modifiers: null);
                if (mi == null) continue;
                var val = mi.Invoke(_threadStore, new object[] { guildId, userId });
                if (val is ulong u && u != 0) return u;
                if (val is string s && ulong.TryParse(s, out var us) && us != 0) return us;
            }
        }
        catch { }
        return 0;
    }

    private static void ThreadStoreSet(ulong guildId, ulong userId, ulong threadId)
    {
        try
        {
            if (_threadStore == null) return;
            var t = _threadStore.GetType();
            foreach (var name in new[] { "SetThreadId", "Set", "Put", "Upsert" })
            {
                var mi = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(ulong), typeof(ulong), typeof(ulong) }, modifiers: null);
                if (mi == null) continue;
                mi.Invoke(_threadStore, new object[] { guildId, userId, threadId });
                return;
            }
        }
        catch { }
    }

    private static async Task<ulong> EnsureDriverThreadAsync(ulong guildId, ulong discordUserId, string label)
    {
        try
        {
            if (_client == null || _dispatchStore == null) return 0;

            var guild = _client.Guilds.FirstOrDefault(g => g.Id == guildId);
            if (guild == null) return 0;

            var settings = _dispatchStore.Get(guildId.ToString());
            if (!ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return 0;

            var dispatchChannel = guild.GetTextChannel(dispatchChId);
            if (dispatchChannel == null) return 0;

            var existing = ThreadStoreTryGet(guildId, discordUserId);
            if (existing != 0) return existing;

            var starter = await dispatchChannel.SendMessageAsync($"📌 Dispatch thread created for **{label}**.");
            var thread = await dispatchChannel.CreateThreadAsync(
                name: $"dispatch-{SanitizeThreadName(label)}",
                autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                type: ThreadType.PrivateThread,
                invitable: false,
                message: starter
            );

            try
            {
                var u = guild.GetUser(discordUserId);
                if (u != null) await thread.AddUserAsync(u);
            }
            catch { }

            ThreadStoreSet(guildId, discordUserId, thread.Id);
            Console.WriteLine($"[THREAD-CREATE] guild={guildId} user={discordUserId} thread={thread.Id}");
            return thread.Id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[THREAD-CREATE] ❌ {ex}");
            return 0;
        }
    }

    private static string SanitizeThreadName(string s)
    {
        s = (s ?? "driver").Trim().ToLowerInvariant();
        if (s.Length > 32) s = s.Substring(0, 32);
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "driver" : safe;
    }

    private static ulong? TryParseChannelIdFromMention(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("<#") && raw.EndsWith(">"))
            raw = raw.Substring(2, raw.Length - 3);
        return ulong.TryParse(raw, out var id) ? id : null;
    }

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
