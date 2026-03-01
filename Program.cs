// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Two-way chat: Discord Thread ↔ ELD
// ✅ Fetch thread messages (+ attachments), Send replies, Upload files, Mark Read, Delete (single/bulk)
// ✅ Railway-safe: REST fallback when channel/thread not in socket cache
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ ADD: Compatibility endpoints to prevent ELD 404s (/api/vtc/name, /api/vtc/status, /api/servers, etc.)
// ✅ Keeps your existing thread-router integration: ThreadMapStore + DiscordThreadRouter + LinkThreadCommand

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Thread map store (key-based: "{guildId}:{discordUserId}" -> threadId)
    private static ThreadMapStore? _threadStore;

    // Storage folder (Railway container-safe)
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

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
        // These DO NOT change your current architecture; they only alias common legacy routes.
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

        // Legacy: "status" checks
        app.MapGet("/api/vtc/status", () => Results.Json(GetStatusPayload(), JsonWriteOpts));
        app.MapGet("/api/status", () => Results.Json(GetStatusPayload(), JsonWriteOpts));
        app.MapGet("/api/discord/status", () => Results.Json(GetStatusPayload(), JsonWriteOpts));

        // Legacy: "name" / "vtc" checks
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

        // Legacy: servers list aliases
        app.MapGet("/api/servers", () => Results.Redirect("/api/vtc/servers", permanent: false));
        app.MapGet("/api/discord/servers", () => Results.Redirect("/api/vtc/servers", permanent: false));
        app.MapGet("/api/vtc/guilds", () => Results.Redirect("/api/vtc/servers", permanent: false));

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
    // Discord commands (kept minimal)
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (_client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        // ✅ Keep your existing thread link behavior
        if (_threadStore != null)
        {
            var handled = await LinkThreadCommand.TryHandleAsync(msg, _client, _threadStore);
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
