// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Merged build: ELD Login + Messaging + !Commands + Announcements
// ✅ CRITICAL FIX: GET /api/messages returns ROOT ARRAY (ELD ParseMessages requires array)
// ✅ Keeps /api/vtc/servers (servers[].id) and /api/vtc/name (fix login 404)
// ✅ Keeps ELD -> Discord send: POST /api/messages/send
// ✅ Keeps thread by-user endpoints + bulk mark/delete
// ✅ Keeps !setupdispatch + !announcement working (MessageReceived wired)
// ✅ No RestWebhook.Url (build webhook URL from Id+Token)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

using OverWatchELD.VtcBot.Threads;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static volatile bool _discordReady = false;

    // ✅ 2-line guard support (prevents duplicate MessageReceived handlers)
    private static bool _messageHandlerAttached = false;

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

    private static DispatchSettingsStore? _dispatchStore;
    private static readonly string DispatchCfgPath = Path.Combine(DataDir, "dispatch_settings.json");

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
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";

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

        public void SetDispatchWebhook(string guildId, string url)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchWebhookUrl = (url ?? "").Trim();
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

        public void SetAnnouncementWebhook(string guildId, string url)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementWebhookUrl = (url ?? "").Trim();
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

    private sealed class SendMessageReq
    {
        [JsonPropertyName("driverName")]
        public string? DisplayName { get; set; }
        public string? DiscordUserId { get; set; }
        public string? DiscordUsername { get; set; }
        public string Text { get; set; } = "";
        public string? Source { get; set; }
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
            Console.WriteLine("✅ Discord client READY");
            return Task.CompletedTask;
        };

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {msg.Severity,-7} {msg.Source,-12} {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        };

        // ✅ 2-line guard (prevents duplicate MessageReceived attaches)
        if (!_messageHandlerAttached)
        {
            _client.MessageReceived += HandleMessageAsync;
            _messageHandlerAttached = true;
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var portStr = (Environment.GetEnvironmentVariable("PORT") ?? "8080").Trim();
        if (!int.TryParse(portStr, out var port)) port = 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        var api = app.MapGroup("/api");
        var api2 = app.MapGroup("/api/api"); // double-api safety
        RegisterRoutes(api);
        RegisterRoutes(api2);

        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        await Task.Delay(-1);
    }

    private static void RegisterRoutes(IEndpointRouteBuilder r)
    {
        // -----------------------------
        // ✅ ELD LOGIN endpoints
        // -----------------------------
        r.MapGet("/vtc/servers", () =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var servers = _client.Guilds.Select(g => new
            {
                id = g.Id.ToString(), // ✅ ELD expects `id`
                name = g.Name,
                guildId = g.Id.ToString()
            }).ToArray();

            return Results.Json(new { ok = true, servers, serverCount = servers.Length }, JsonWriteOpts);
        });

        r.MapGet("/vtc/name", (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();

            SocketGuild? g = null;
            if (ulong.TryParse(guildIdStr, out var gid) && gid != 0)
                g = _client.Guilds.FirstOrDefault(x => x.Id == gid);

            g ??= _client.Guilds.FirstOrDefault();
            if (g == null)
                return Results.Json(new { ok = false, error = "NoGuild" }, statusCode: 404);

            return Results.Json(new
            {
                ok = true,
                guildId = g.Id.ToString(),
                name = g.Name,
                vtcName = g.Name
            }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Announcements feed + post
        // -----------------------------
        r.MapGet("/vtc/announcements", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var limitStr = (req.Query["limit"].ToString() ?? "25").Trim();
            if (!int.TryParse(limitStr, out var limit)) limit = 25;
            limit = Math.Clamp(limit, 1, 100);

            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guild.Id.ToString());
            if (settings == null || !ulong.TryParse(settings.AnnouncementChannelId, out var annChId) || annChId == 0)
                return Results.Json(new { ok = true, guildId = guild.Id.ToString(), announcements = Array.Empty<object>() }, JsonWriteOpts);

            var ch = guild.GetTextChannel(annChId);
            if (ch == null)
                return Results.Json(new { ok = true, guildId = guild.Id.ToString(), announcements = Array.Empty<object>() }, JsonWriteOpts);

            var msgs = await ch.GetMessagesAsync(limit: limit).FlattenAsync();

            var announcements = msgs
                .OrderByDescending(m => m.Timestamp)
                .Select(m =>
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
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId = guild.Id.ToString(), announcements }, JsonWriteOpts);
        });

        r.MapPost("/vtc/announcements/post", async (HttpRequest req) =>
        {
            AnnouncementPostReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<AnnouncementPostReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            var gidStr = (payload?.GuildId ?? "").Trim();
            var text = (payload?.Text ?? "").Trim();
            var author = (payload?.Author ?? "").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "EmptyText" }, statusCode: 400);

            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guild.Id.ToString());
            var hookUrl = (settings?.AnnouncementWebhookUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(hookUrl))
                return Results.Json(new { ok = false, error = "AnnouncementWebhookNotSet" }, statusCode: 400);

            var content = string.IsNullOrWhiteSpace(author) ? text : $"**{author}:** {text}";
            var json = JsonSerializer.Serialize(new { username = "OverWatch ELD", content }, JsonWriteOpts);

            using var resp = await _http.PostAsync(hookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return Results.Json(new { ok = false, error = "WebhookSendFailed", status = (int)resp.StatusCode, body }, statusCode: 502);

            return Results.Json(new { ok = true }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ CRITICAL: GET /api/messages MUST return ROOT ARRAY (ELD ParseMessages requires array)
        // -----------------------------
        r.MapGet("/messages", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null) return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var settings = _dispatchStore?.Get(guild.Id.ToString());
            if (settings == null || !ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var ch = guild.GetTextChannel(dispatchChId);
            if (ch == null) return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var msgs = await ch.GetMessagesAsync(limit: 50).FlattenAsync();

            // Return oldest -> newest
            var arr = msgs
                .Where(m =>
                {
                    var txt = (m.Content ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(txt)) return false;

                    // ✅ don't feed setup/command spam into the ELD
                    if (txt.StartsWith("!", StringComparison.OrdinalIgnoreCase)) return false;

                    return true;
                })
                .OrderBy(m => m.Timestamp)
                .Select(m =>
                {
                    long createdUnix = 0;
                    try { createdUnix = m.Timestamp.ToUnixTimeSeconds(); } catch { }

                    var author = (m.Author?.Username ?? "User").Trim();
                    var content = (m.Content ?? "").Trim();

                    // ✅ Make it appear as "Dispatch" to the ELD
                    var eldText = $"[{author}] {content}";

                    return new
                    {
                        id = m.Id.ToString(),                 // ✅ string (ELD requires)
                        createdUnix = createdUnix.ToString(), // ✅ extra-safe for strict parsers
                        sentUtc = m.Timestamp.UtcDateTime.ToString("o"),

                        fromName = "Dispatch",
                        senderName = "Dispatch",
                        text = eldText,

                        isDispatcher = true,
                        avatarUrl = ""
                    };
                })
                .ToArray();

            return Results.Json(arr, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ POST /api/messages/send (ELD -> Discord)
        // -----------------------------
        r.MapPost("/messages/send", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();

            SendMessageReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<SendMessageReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Text))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var who = NormalizeDisplayName(payload.DisplayName, payload.DiscordUsername);
            var text = payload.Text.Trim();

            // Prefer thread if discordUserId is known
            var discordUserIdStr = (payload.DiscordUserId ?? "").Trim();
            if (ulong.TryParse(discordUserIdStr, out var duid) && duid != 0)
            {
                var threadId = ThreadStoreTryGet(guild.Id, duid);
                if (threadId == 0)
                {
                    var created = await EnsureDriverThreadAsync(guild, duid, who);
                    if (created == 0)
                        return Results.Json(new { ok = false, error = "ThreadCreateFailedOrDispatchNotSet" }, statusCode: 500);
                    threadId = created;
                }

                var chan = await ResolveChannelAsync(threadId);
                if (chan == null)
                    return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

                await EnsureThreadOpenAsync(chan);

                var sent = await chan.SendMessageAsync($"**{who}:** {text}");
                return Results.Json(new { ok = true, mode = "thread", threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
            }

            // Fallback: dispatch webhook if set
            var settings = _dispatchStore?.Get(guild.Id.ToString());
            var hookUrl = (settings?.DispatchWebhookUrl ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(hookUrl))
            {
                var json = JsonSerializer.Serialize(new { username = who, content = text }, JsonWriteOpts);
                using var resp = await _http.PostAsync(hookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { ok = false, error = "DispatchWebhookSendFailed", status = (int)resp.StatusCode, body }, statusCode: 502);

                return Results.Json(new { ok = true, mode = "dispatchWebhook" }, JsonWriteOpts);
            }

            // Final fallback: dispatch channel send
            if (settings == null || !ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return Results.Json(new { ok = false, error = "DispatchNotConfigured" }, statusCode: 400);

            var dispatchCh = guild.GetTextChannel(dispatchChId);
            if (dispatchCh == null)
                return Results.Json(new { ok = false, error = "DispatchChannelNotFound" }, statusCode: 404);

            var sent2 = await dispatchCh.SendMessageAsync($"**{who}:** {text}");
            return Results.Json(new { ok = true, mode = "dispatchChannel", messageId = sent2.Id.ToString() }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Bulk mark/delete (kept as-is)
        // -----------------------------
        r.MapPost("/messages/markread/bulk", async (HttpRequest req) =>
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

        r.MapDelete("/messages/delete/bulk", async (HttpRequest req) =>
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
    }

    // -----------------------------
    // Discord prefix commands
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (_client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;

        try { if (_client.CurrentUser != null && msg.Author.Id == _client.CurrentUser.Id) return; } catch { }

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

        if (content.Equals("!ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (msg.Channel is not SocketGuildChannel guildChan)
        {
            if (content.Equals("!help", StringComparison.OrdinalIgnoreCase))
                await msg.Channel.SendMessageAsync("Use !setupdispatch / !announcement inside a server.");
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

        if (cmd == "help")
        {
            await msg.Channel.SendMessageAsync(
                "Commands:\n" +
                "• !setupdispatch #channel (admin)\n" +
                "• !setdispatchwebhook <url> (admin)\n" +
                "• !announcement #channel (admin)\n" +
                "• !setannouncementwebhook <url> (admin)\n" +
                "• !ping\n"
            );
            return;
        }

        if (!isAdmin)
        {
            await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server/Admin required).");
            return;
        }

        if (_dispatchStore == null)
        {
            await msg.Channel.SendMessageAsync("❌ Dispatch store not initialized.");
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

                _dispatchStore.SetDispatchChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync("✅ Channel set. Webhook token missing; copy URL in Discord and run `!setdispatchwebhook <url>`");
                    return;
                }

                _dispatchStore.SetDispatchWebhook(guildIdStr, url);
                await msg.Channel.SendMessageAsync($"✅ Dispatch linked: <#{ch.Id}>");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Webhook create failed (need Manage Webhooks). {ex.Message}");
            }
            return;
        }

        if (cmd == "setdispatchwebhook")
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await msg.Channel.SendMessageAsync("Usage: `!setdispatchwebhook https://discord.com/api/webhooks/...`"); return; }

            _dispatchStore.SetDispatchWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Dispatch webhook saved.");
            return;
        }

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

                _dispatchStore.SetAnnouncementChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync("✅ Channel set. Webhook token missing; copy URL in Discord and run `!setannouncementwebhook <url>`");
                    return;
                }

                _dispatchStore.SetAnnouncementWebhook(guildIdStr, url);
                await msg.Channel.SendMessageAsync($"✅ Announcements linked: <#{ch.Id}>");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Webhook create failed (need Manage Webhooks). {ex.Message}");
            }
            return;
        }

        if (cmd == "setannouncementwebhook")
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await msg.Channel.SendMessageAsync("Usage: `!setannouncementwebhook https://discord.com/api/webhooks/...`"); return; }

            _dispatchStore.SetAnnouncementWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Announcement webhook saved.");
            return;
        }

        await msg.Channel.SendMessageAsync("Unknown command. Use `!help`.");
    }

    private static SocketGuild? ResolveGuild(string gidStr)
    {
        if (_client == null) return null;

        if (ulong.TryParse((gidStr ?? "").Trim(), out var gid) && gid != 0)
        {
            var g = _client.Guilds.FirstOrDefault(x => x.Id == gid);
            if (g != null) return g;
        }

        return _client.Guilds.FirstOrDefault();
    }

    private static string NormalizeDisplayName(string? requested, string? discordUsername)
    {
        var dn = (requested ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(dn) && !dn.Equals("User", StringComparison.OrdinalIgnoreCase))
            return dn;

        var du = (discordUsername ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(du))
            return du;

        return string.IsNullOrWhiteSpace(dn) ? "User" : dn;
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

    // Thread mapping helpers (reflection-safe)
    private static ulong ThreadStoreTryGet(ulong guildId, ulong userId)
    {
        try
        {
            if (_threadStore == null) return 0;
            var t = _threadStore.GetType();

            foreach (var name in new[] { "TryGetThreadId", "GetThreadId", "TryGet", "Get" })
            {
                var mi = t.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(ulong), typeof(ulong) },
                    modifiers: null);

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
                var mi = t.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(ulong), typeof(ulong), typeof(ulong) },
                    modifiers: null);

                if (mi == null) continue;
                mi.Invoke(_threadStore, new object[] { guildId, userId, threadId });
                return;
            }
        }
        catch { }
    }

    private static async Task<ulong> EnsureDriverThreadAsync(SocketGuild guild, ulong discordUserId, string label)
    {
        try
        {
            if (_dispatchStore == null) return 0;

            var settings = _dispatchStore.Get(guild.Id.ToString());
            if (!ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return 0;

            var dispatchChannel = guild.GetTextChannel(dispatchChId);
            if (dispatchChannel == null) return 0;

            var existing = ThreadStoreTryGet(guild.Id, discordUserId);
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

            ThreadStoreSet(guild.Id, discordUserId, thread.Id);
            return thread.Id;
        }
        catch { return 0; }
    }

    private static string SanitizeThreadName(string s)
    {
        s = (s ?? "driver").Trim().ToLowerInvariant();
        if (s.Length > 32) s = s.Substring(0, 32);
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "driver" : safe;
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
