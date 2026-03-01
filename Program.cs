// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Public-release safe: NO guild hardcoding, NO personal Discord name output
// ✅ Commands: !ping, !setdispatchchannel
// ✅ NEW: Option A auto-thread per user under dispatch channel (via /api/messages/send)
// ✅ NEW: Option B manual override: !linkthread (run inside thread)
// ✅ NEW: Admin-only persistent webhooks (multi-key): !setwebhook / !delwebhook / !listwebhooks / !testwebhook / !setdispatchwebhook
// ✅ NEW: Admin-only persistent announcement channel: !announcement
// ✅ NEW: Admin-only persistent roster: !adddriver <Name> <Role...>  and  !remove <Name>

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

    // ✅ Roster (per guild) — persistent
    private static readonly object DriversGate = new();
    private static readonly ConcurrentDictionary<string, List<DriverItem>> GuildDrivers = new();

    // Storage folder (Railway container-safe)
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string GuildCfgPath = Path.Combine(DataDir, "guild_cfg.json");

    // Thread map file
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

    // ✅ Drivers file
    private static readonly string DriversPath = Path.Combine(DataDir, "drivers.json");

    // -----------------------------
    // Guild configuration
    // -----------------------------
    private sealed class GuildCfg
    {
        public string GuildId { get; set; } = "";
        public string DispatchChannelId { get; set; } = "";

        // Legacy single webhook (kept for backwards compatibility)
        public string DispatchWebhookUrl { get; set; } = "";

        // ✅ Multi-webhook store (key -> url), persisted in guild_cfg.json
        public Dictionary<string, string> Webhooks { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ✅ Announcement channel (persisted)
        public string AnnouncementChannelId { get; set; } = "";

        public string VtcName { get; set; } = "";
    }

    // ✅ Roster driver item
    private sealed class DriverItem
    {
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
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

        // Load configs + roster
        LoadGuildCfgs();
        LoadDrivers();

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

        app.MapGet("/api/vtc/servers", () =>
{
    if (_client == null || !_discordReady)
        return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

    var servers = _client.Guilds.Select(g => new
    {
        guildId = g.Id.ToString(),
        name = g.Name
    }).ToArray();

    return Results.Json(new { ok = true, servers }, JsonWriteOpts);
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

        // -----------------------------
        // ✅ Basic commands
        // -----------------------------
        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        // -----------------------------
        // ✅ Admin-only: set dispatch channel
        // -----------------------------
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

            if (!IsAdmin(msg.Author))
            {
                await msg.Channel.SendMessageAsync("⛔ Admin only.");
                return;
            }

            var cfg = GetOrCreateGuildCfg(guild.Id.ToString());
            cfg.DispatchChannelId = channelId.Value.ToString();
            SaveGuildCfgs();

            await msg.Channel.SendMessageAsync($"✅ Dispatch channel saved: {channelLabel}");
            return;
        }

        // -----------------------------
        // ✅ Admin-only: Announcement channel link
        // Command: !announcement  (run inside the channel you want as announcements)
        // -----------------------------
        if (body.Equals("announcement", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetGuildAndChannel(msg, out var guild, out var channelId, out var label))
            {
                await msg.Channel.SendMessageAsync("❌ Run this inside a server channel.");
                return;
            }

            if (!IsAdmin(msg.Author))
            {
                await msg.Channel.SendMessageAsync("⛔ Admin only.");
                return;
            }

            var cfg = GetOrCreateGuildCfg(guild.Id.ToString());
            cfg.AnnouncementChannelId = channelId.ToString();
            SaveGuildCfgs();

            await msg.Channel.SendMessageAsync($"✅ Announcement channel saved: {label}");
            return;
        }

        // -----------------------------
        // ✅ Admin-only: Webhook commands (persistent)
        // -----------------------------
        if (body.StartsWith("setwebhook ", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("delwebhook ", StringComparison.OrdinalIgnoreCase) ||
            body.Equals("listwebhooks", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("testwebhook ", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("setdispatchwebhook ", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetGuildId(msg, out var guildId))
            {
                await msg.Channel.SendMessageAsync("❌ Run this inside a server channel.");
                return;
            }

            if (!IsAdmin(msg.Author))
            {
                await msg.Channel.SendMessageAsync("⛔ Admin only.");
                return;
            }

            var cfg = GetOrCreateGuildCfg(guildId.ToString());
            cfg.Webhooks ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // !setdispatchwebhook <url>  (alias => key "dispatch")
            if (body.StartsWith("setdispatchwebhook ", StringComparison.OrdinalIgnoreCase))
            {
                var url = body["setdispatchwebhook ".Length..].Trim();
                if (!IsProbablyDiscordWebhook(url))
                {
                    await msg.Channel.SendMessageAsync("❌ That doesn't look like a Discord webhook URL.");
                    return;
                }

                cfg.Webhooks["dispatch"] = url;
                cfg.DispatchWebhookUrl = url; // keep legacy field synced
                SaveGuildCfgs();
                await msg.Channel.SendMessageAsync("✅ Saved webhook key **dispatch**.");
                return;
            }

            // !setwebhook <key> <url>
            if (body.StartsWith("setwebhook ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitArgs(body);
                if (parts.Count < 3)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!setwebhook <key> <url>`");
                    return;
                }

                var key = parts[1].Trim();
                var url = parts[2].Trim();

                if (string.IsNullOrWhiteSpace(key))
                {
                    await msg.Channel.SendMessageAsync("❌ Missing key. Example: `!setwebhook dispatch <url>`");
                    return;
                }

                if (!IsProbablyDiscordWebhook(url))
                {
                    await msg.Channel.SendMessageAsync("❌ That doesn't look like a Discord webhook URL.");
                    return;
                }

                cfg.Webhooks[key] = url;

                if (key.Equals("dispatch", StringComparison.OrdinalIgnoreCase))
                    cfg.DispatchWebhookUrl = url;

                SaveGuildCfgs();
                await msg.Channel.SendMessageAsync($"✅ Saved webhook key **{key}**.");
                return;
            }

            // !delwebhook <key>
            if (body.StartsWith("delwebhook ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitArgs(body);
                if (parts.Count < 2)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!delwebhook <key>`");
                    return;
                }

                var key = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    await msg.Channel.SendMessageAsync("❌ Missing key.");
                    return;
                }

                var removed = cfg.Webhooks.Remove(key);

                if (key.Equals("dispatch", StringComparison.OrdinalIgnoreCase))
                    cfg.DispatchWebhookUrl = "";

                SaveGuildCfgs();
                await msg.Channel.SendMessageAsync(removed ? $"✅ Removed webhook key **{key}**." : $"No webhook key **{key}** found.");
                return;
            }

            // !listwebhooks
            if (body.Equals("listwebhooks", StringComparison.OrdinalIgnoreCase))
            {
                if (cfg.Webhooks.Count == 0)
                {
                    await msg.Channel.SendMessageAsync("No webhooks saved for this server yet.");
                    return;
                }

                var lines = cfg.Webhooks
                    .OrderBy(k => k.Key)
                    .Select(kvp => $"- **{kvp.Key}** = `{MaskUrl(kvp.Value)}`");

                await msg.Channel.SendMessageAsync("Saved webhooks:\n" + string.Join("\n", lines));
                return;
            }

            // !testwebhook <key> <message...>
            if (body.StartsWith("testwebhook ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitArgs(body);
                if (parts.Count < 3)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!testwebhook <key> <message>`");
                    return;
                }

                var key = parts[1].Trim();
                var message = string.Join(' ', parts.Skip(2)).Trim();

                if (!cfg.Webhooks.TryGetValue(key, out var url) || string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync($"No webhook key **{key}** found.");
                    return;
                }

                try
                {
                    await SendViaWebhookAsync(url, "OverWatch ELD", message);
                    await msg.Channel.SendMessageAsync($"✅ Sent test message to **{key}**.");
                }
                catch
                {
                    await msg.Channel.SendMessageAsync($"❌ Failed to post to **{key}**.");
                }
                return;
            }
        }

        // -----------------------------
        // ✅ Admin-only: Roster commands
        // !adddriver "Name Here" Role Here
        // !remove "Name Here"
        // -----------------------------
        if (body.StartsWith("adddriver", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetGuildId(msg, out var guildId))
            {
                await msg.Channel.SendMessageAsync("❌ Run this inside a server channel.");
                return;
            }

            if (!IsAdmin(msg.Author))
            {
                await msg.Channel.SendMessageAsync("⛔ Admin only.");
                return;
            }

            var args = SplitArgs(body);

            // !adddriver <Name> <Role...>
            if (args.Count > 0 && args[0].Equals("adddriver", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count < 3)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!adddriver \"Name\" Role`  (quotes optional if no spaces)");
                    return;
                }

                var name = args[1].Trim();
                var role = string.Join(' ', args.Skip(2)).Trim();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(role))
                {
                    await msg.Channel.SendMessageAsync("❌ Name and Role are required.");
                    return;
                }

                lock (DriversGate)
                {
                    var key = guildId.ToString();
                    var list = GuildDrivers.GetOrAdd(key, _ => new List<DriverItem>());

                    // upsert by name (case-insensitive)
                    var existing = list.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Role = role;
                        // keep original AddedUtc (or refresh it—your call; leaving it)
                    }
                    else
                    {
                        list.Add(new DriverItem
                        {
                            Name = name,
                            Role = role,
                            AddedUtc = DateTimeOffset.UtcNow
                        });
                    }

                    SaveDriversUnsafe();
                }

                await msg.Channel.SendMessageAsync($"✅ Driver saved: **{name}** — *{role}*");
                return;
            }

            // !remove <Name...>
            if (args.Count > 0 && args[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count < 2)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!remove \"Name\"`");
                    return;
                }

                var name = string.Join(' ', args.Skip(1)).Trim().Trim('"');

                if (string.IsNullOrWhiteSpace(name))
                {
                    await msg.Channel.SendMessageAsync("❌ Name is required.");
                    return;
                }

                bool removed;
                lock (DriversGate)
                {
                    var key = guildId.ToString();
                    if (!GuildDrivers.TryGetValue(key, out var list))
                    {
                        removed = false;
                    }
                    else
                    {
                        var before = list.Count;
                        list.RemoveAll(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        removed = list.Count != before;
                        if (removed) SaveDriversUnsafe();
                    }
                }

                await msg.Channel.SendMessageAsync(removed
                    ? $"✅ Removed driver **{name}**."
                    : $"No driver named **{name}** found.");
                return;
            }
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

                // Ensure dictionary exists (older files won't have it)
                cfg.Webhooks ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // If legacy field exists but dictionary doesn't have "dispatch", import it
                if (!string.IsNullOrWhiteSpace(cfg.DispatchWebhookUrl) && !cfg.Webhooks.ContainsKey("dispatch"))
                    cfg.Webhooks["dispatch"] = cfg.DispatchWebhookUrl;

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

            // keep legacy field synced on save if possible
            foreach (var cfg in GuildCfgs.Values)
            {
                cfg.Webhooks ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (cfg.Webhooks.TryGetValue("dispatch", out var durl))
                    cfg.DispatchWebhookUrl = durl ?? "";
            }

            var list = GuildCfgs.Values
                .OrderBy(x => x.GuildId)
                .ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GuildCfgPath, json);
        }
        catch { }
    }

    // -----------------------------
    // Drivers persistence
    // -----------------------------
    private sealed class DriversFileModel
    {
        public List<GuildDriversBlock> Guilds { get; set; } = new();
    }

    private sealed class GuildDriversBlock
    {
        public string GuildId { get; set; } = "";
        public List<DriverItem> Drivers { get; set; } = new();
    }

    private static void LoadDrivers()
    {
        try
        {
            if (!File.Exists(DriversPath)) return;

            var json = File.ReadAllText(DriversPath);
            var model = JsonSerializer.Deserialize<DriversFileModel>(json, JsonReadOpts) ?? new DriversFileModel();

            lock (DriversGate)
            {
                GuildDrivers.Clear();

                foreach (var g in model.Guilds ?? new List<GuildDriversBlock>())
                {
                    var gid = (g.GuildId ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(gid)) continue;

                    var list = (g.Drivers ?? new List<DriverItem>())
                        .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Name))
                        .ToList();

                    GuildDrivers[gid] = list;
                }
            }
        }
        catch { }
    }

    private static void SaveDriversUnsafe()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            var model = new DriversFileModel
            {
                Guilds = GuildDrivers
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => new GuildDriversBlock
                    {
                        GuildId = kvp.Key,
                        Drivers = kvp.Value
                            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    })
                    .ToList()
            };

            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DriversPath, json);
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

    // -----------------------------
    // Helpers
    // -----------------------------
    private static bool TryGetGuildId(SocketUserMessage msg, out ulong guildId)
    {
        guildId = 0;

        if (msg.Channel is SocketTextChannel tc)
        {
            guildId = tc.Guild.Id;
            return true;
        }

        if (msg.Channel is SocketThreadChannel th)
        {
            guildId = th.Guild.Id;
            return true;
        }

        return false;
    }

    private static bool TryGetGuildAndChannel(SocketUserMessage msg, out SocketGuild guild, out ulong channelId, out string label)
    {
        guild = null!;
        channelId = 0;
        label = "";

        if (msg.Channel is SocketTextChannel tc)
        {
            guild = tc.Guild;
            channelId = tc.Id;
            label = $"#{tc.Name}";
            return true;
        }

        if (msg.Channel is SocketThreadChannel th)
        {
            guild = th.Guild;
            channelId = th.ParentChannel?.Id ?? th.Id;
            label = th.ParentChannel != null ? $"#{th.ParentChannel.Name} (parent of thread)" : $"(thread parent unknown)";
            return true;
        }

        return false;
    }

    private static bool IsAdmin(SocketUser user)
    {
        if (user is not SocketGuildUser gu) return false;
        var p = gu.GuildPermissions;
        return p.Administrator || p.ManageGuild;
    }

    private static bool IsProbablyDiscordWebhook(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        return url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://ptb.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://canary.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    private static string MaskUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.Length <= 18) return url;
        return url.Substring(0, 12) + "..." + url.Substring(Math.Max(0, url.Length - 6));
    }

    // Splits command args with quote support:
    // !adddriver "John Doe" Company Driver
    private static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            result.Add(sb.ToString());

        return result;
    }
}
