// Program.cs  ✅ FULL COPY/REPLACE
// ✅ GLOBAL BOT, MULTI-VTC (per Discord server / Guild)
// ✅ Per-guild persistence: linked_drivers_<guildId>.json
// ✅ Per-guild VTC name resolution: /api/vtc/name?guildId=...
// ✅ Link codes are tied to a guild; DMs are blocked for linking
// ✅ Railway env overrides: DISCORD_TOKEN/BOT_TOKEN + PORT + optional VTC_NAME/DEFAULT_DRIVER_NAME
// ✅ Backwards-friendly: /api/vtc/servers still works without guildId

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "vtcbot.log");

    private static DiscordSocketClient? _client;
    private static BotConfig _cfg = BotConfig.LoadOrDefault();

    // Presence ping (optional)
    private static PresencePing? _lastPresence;

    // ✅ Link codes -> Discord user + guild (in-memory)
    private static readonly ConcurrentDictionary<string, LinkRecord> _linkCodes = new(StringComparer.OrdinalIgnoreCase);

    // ✅ Linked drivers (persisted PER GUILD)
    // guildId -> (driverKey -> record)
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<string, LinkedDriverRecord>> _linkedByGuild = new();

    private static void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log("🔥 UNHANDLED EXCEPTION:");
            Log(e.ExceptionObject?.ToString() ?? "(null)");
        };

        // ------------------------------------------------------------
        // ✅ Railway/Host ENV overrides (DISCORD_TOKEN + PORT etc.)
        // ------------------------------------------------------------
        ApplyEnvironmentOverrides();

        var cfgPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        Log($"Runtime BaseDirectory: {AppContext.BaseDirectory}");
        Log($"Config path: {cfgPath}");
        Log($"Config exists: {File.Exists(cfgPath)}");

        if (_cfg.Port <= 0) _cfg.Port = 8080;

        Log("Starting OverWatchELD.VtcBot (GLOBAL BOT / multi-VTC per guild + LINK + LINKED-ONLY ROSTER + PERSIST)...");
        Log($"Config: Port={_cfg.Port}  Token={(string.IsNullOrWhiteSpace(_cfg.BotToken) ? "MISSING" : "OK")}");

        // Start Discord (optional)
        if (!string.IsNullOrWhiteSpace(_cfg.BotToken))
        {
            try
            {
                var discordCfg = new DiscordSocketConfig
                {
                    GatewayIntents =
                        GatewayIntents.Guilds |
                        GatewayIntents.GuildMembers |
                        GatewayIntents.GuildMessages |
                        GatewayIntents.DirectMessages |
                        GatewayIntents.MessageContent,
                    AlwaysDownloadUsers = true,
                    LogLevel = LogSeverity.Info
                };

                _client = new DiscordSocketClient(discordCfg);

                _client.Log += msg =>
                {
                    Log($"[DISCORD] {msg.Severity}: {msg.Message} {msg.Exception?.Message}".Trim());
                    return Task.CompletedTask;
                };

                _client.JoinedGuild += g =>
                {
                    Log($"✅ JoinedGuild: {g.Name} ({g.Id})");
                    // Load persisted roster for this guild (if present)
                    LoadLinkedDriversFromDisk(g.Id);
                    return Task.CompletedTask;
                };

                _client.LeftGuild += g =>
                {
                    Log($"⚠️ LeftGuild: {g.Name} ({g.Id})");
                    // keep any local files; no action required
                    return Task.CompletedTask;
                };

                _client.Ready += async () =>
                {
                    Log("✅ Discord client READY.");
                    Log($"✅ Logged in as: {_client.CurrentUser?.Username}#{_client.CurrentUser?.Discriminator}");
                    Log($"✅ Guilds visible: {_client.Guilds.Count}");

                    // Load persisted rosters for all guilds we can see
                    foreach (var g in _client.Guilds)
                    {
                        LoadLinkedDriversFromDisk(g.Id);
                    }

                    // Optional: download users (helps display names)
                    try
                    {
                        foreach (var g in _client.Guilds)
                        {
                            try
                            {
                                await g.DownloadUsersAsync();
                                Log($"✅ DownloadUsersAsync complete for {g.Name} ({g.Id}). Cached members: {g.Users.Count}");
                            }
                            catch (Exception ex)
                            {
                                Log($"⚠️ DownloadUsersAsync failed for {g.Name} ({g.Id}): {ex.Message}");
                            }
                        }
                    }
                    catch { }

                    return;
                };

                _client.Disconnected += ex =>
                {
                    Log("⚠️ Discord client DISCONNECTED.");
                    if (ex != null) Log(ex.ToString());
                    return Task.CompletedTask;
                };

                // ✅ LINK command support
                _client.MessageReceived += OnMessageReceivedAsync;

                await _client.LoginAsync(TokenType.Bot, _cfg.BotToken);
                await _client.StartAsync();
                Log("✅ Discord client started.");
            }
            catch (Exception ex)
            {
                Log("🔥 Discord startup FAILED:");
                Log(ex.ToString());
                _client = null; // still start HTTP
            }
        }
        else
        {
            Log("⚠️ BotToken missing. Discord will not start.");
        }

        await RunHttpApiAsync(_cfg.Port);
    }

    // ✅ Reads Railway env vars and overrides config.json
    private static void ApplyEnvironmentOverrides()
    {
        try
        {
            // Token (Railway Variables)
            var envToken =
                Environment.GetEnvironmentVariable("DISCORD_TOKEN") ??
                Environment.GetEnvironmentVariable("BOT_TOKEN");

            if (!string.IsNullOrWhiteSpace(envToken))
            {
                _cfg.BotToken = envToken.Trim();
                Log("✅ DISCORD_TOKEN loaded from environment.");
            }
            else
            {
                Log("⚠️ DISCORD_TOKEN not found in environment (will rely on config.json).");
            }

            // Port (Railway sets PORT)
            var envPort = Environment.GetEnvironmentVariable("PORT");
            if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var p) && p > 0)
            {
                _cfg.Port = p;
                Log($"✅ PORT loaded from environment: {_cfg.Port}");
            }

            // Optional (not required now)
            var envVtcName = Environment.GetEnvironmentVariable("VTC_NAME");
            if (!string.IsNullOrWhiteSpace(envVtcName))
                _cfg.VtcName = envVtcName.Trim();

            var envDefaultDriver = Environment.GetEnvironmentVariable("DEFAULT_DRIVER_NAME");
            if (!string.IsNullOrWhiteSpace(envDefaultDriver))
                _cfg.DefaultDriverName = envDefaultDriver.Trim();
        }
        catch (Exception ex)
        {
            Log("⚠️ ApplyEnvironmentOverrides failed:");
            Log(ex.Message);
        }
    }

    private static async Task OnMessageReceivedAsync(SocketMessage msg)
    {
        try
        {
            if (msg.Author.IsBot) return;

            var content = (msg.Content ?? "").Trim();

            // Support: !link CODE  (also accept /link CODE or "link CODE")
            if (TryParseLinkCode(content, out var code))
            {
                // Must be in a guild (no DMs) so we can tie the code to a VTC
                if (msg.Channel is not SocketGuildChannel gc)
                {
                    await msg.Channel.SendMessageAsync("⚠️ Use `!link CODE` inside your Discord server (not in DMs).");
                    return;
                }

                var guildId = gc.Guild.Id;

                code = NormalizeCode(code);

                var rec = new LinkRecord
                {
                    Code = code,
                    GuildId = guildId,
                    DiscordUserId = msg.Author.Id,
                    DiscordUserName = $"{msg.Author.Username}#{msg.Author.Discriminator}",
                    CreatedUtc = DateTimeOffset.UtcNow
                };

                _linkCodes[code] = rec;

                // ✅ Create/persist a linked roster entry immediately so roster isn't empty
                // Driver becomes "confirmed" when ELD later calls /api/vtc/link/consume
                UpsertLinkedDriverFromDiscord(
                    guildId: guildId,
                    discordUserId: rec.DiscordUserId,
                    fallbackTag: rec.DiscordUserName,
                    confirmed: false,
                    driverName: _cfg.DefaultDriverName
                );

                Log($"✅ LINK set: guild={guildId} code={code} user={msg.Author.Username}({msg.Author.Id})");
                await msg.Channel.SendMessageAsync(
                    "✅ Link code created for this server.\n" +
                    "Go back to the ELD and complete linking there.\n" +
                    "If it asks for a code, use: " + code
                );
                return;
            }

            if (content.Equals("ping", StringComparison.OrdinalIgnoreCase) ||
                content.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("health", StringComparison.OrdinalIgnoreCase))
            {
                await msg.Channel.SendMessageAsync(
                    "OverWatch ELD VTC Bot is online (GLOBAL bot; each Discord server = its own VTC).\n" +
                    $"Health:   http://localhost:{_cfg.Port}/health\n" +
                    $"Servers:  http://localhost:{_cfg.Port}/api/vtc/servers\n" +
                    "Tip: Your ELD must call APIs with a guildId, e.g.\n" +
                    $"/api/vtc/roster?guildId=YOUR_SERVER_ID\n" +
                    $"/api/vtc/name?guildId=YOUR_SERVER_ID\n"
                );
            }
        }
        catch (Exception ex)
        {
            Log("🔥 MessageReceived error:");
            Log(ex.ToString());
        }
    }

    private static bool TryParseLinkCode(string content, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(content)) return false;

        var parts = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var cmd = parts[0].Trim();
        if (!cmd.Equals("!link", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Equals("/link", StringComparison.OrdinalIgnoreCase) &&
            !cmd.Equals("link", StringComparison.OrdinalIgnoreCase))
            return false;

        code = parts[1].Trim();
        return !string.IsNullOrWhiteSpace(code);
    }

    private static string NormalizeCode(string code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        c = new string(c.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
        return c;
    }

    private static string NormalizeDriverKey(string driver)
    {
        var d = (driver ?? "").Trim();
        d = d.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        while (d.Contains("  ")) d = d.Replace("  ", " ");
        return d;
    }

    private static string MakeDiscordKey(ulong discordUserId) => $"discord:{discordUserId}";

    private static string LinkedDriversPathFor(ulong guildId) =>
        Path.Combine(AppContext.BaseDirectory, $"linked_drivers_{guildId}.json");

    private static ConcurrentDictionary<string, LinkedDriverRecord> GetGuildStore(ulong guildId)
    {
        return _linkedByGuild.GetOrAdd(guildId,
            _ => new ConcurrentDictionary<string, LinkedDriverRecord>(StringComparer.OrdinalIgnoreCase));
    }

    private static void UpsertLinkedDriverFromDiscord(
        ulong guildId,
        ulong discordUserId,
        string fallbackTag,
        bool confirmed,
        string? driverName)
    {
        try
        {
            var key = MakeDiscordKey(discordUserId);

            var resolved = TryResolveDiscordName(guildId, discordUserId);
            var display = !string.IsNullOrWhiteSpace(resolved) ? resolved! : (fallbackTag ?? $"DiscordUser-{discordUserId}");

            var store = GetGuildStore(guildId);

            store.AddOrUpdate(
                key,
                _ => new LinkedDriverRecord
                {
                    GuildId = guildId,
                    DriverKey = key,
                    DriverName = string.IsNullOrWhiteSpace(driverName) ? null : driverName,
                    DiscordUserId = discordUserId,
                    DiscordName = display,
                    LinkedUtc = DateTimeOffset.UtcNow,
                    Confirmed = confirmed
                },
                (_, existing) =>
                {
                    existing.GuildId = guildId;
                    existing.DiscordUserId = discordUserId;
                    existing.DiscordName = display;
                    if (!string.IsNullOrWhiteSpace(driverName))
                        existing.DriverName = driverName;
                    if (confirmed)
                        existing.Confirmed = true; // only ever promotes to confirmed
                    return existing;
                }
            );

            SaveLinkedDriversToDisk(guildId);
        }
        catch { }
    }

    // ------------------------------------------------------------
    // HTTP API (HttpListener)
    // ------------------------------------------------------------
    private static async Task RunHttpApiAsync(int port)
    {
        var prefixes = new[]
{
    // ✅ Railway-safe: listen on all interfaces (NOT localhost)
    $"http://0.0.0.0:{port}/",
    $"http://*:{port}/"
};

        HttpListener? listener = null;
        Exception? last = null;

        foreach (var pre in prefixes)
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(pre);
                listener.Start();
                Log($"✅ HTTP API listening on {pre}");
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                try { listener?.Close(); } catch { }
                listener = null;
            }
        }

        if (listener == null)
        {
            Log("🔥 HTTP API FAILED to start on any prefix.");
            Log(last?.ToString() ?? "(no exception)");
            Log("If you see Access Denied, run as Administrator OR reserve URL ACL:");
            Log($"  netsh http add urlacl url=http://+:{port}/ user=Everyone");
            return;
        }

        Log($"Try: http://localhost:{port}/");
        Log($"Try: http://localhost:{port}/health");
        Log($"Try: http://localhost:{port}/api/vtc/servers");
        Log($"Try: http://localhost:{port}/api/vtc/name?guildId=GUILD_ID");
        Log($"Try: http://localhost:{port}/api/vtc/roster?guildId=GUILD_ID");
        Log($"Try: http://localhost:{port}/api/vtc/link/pending?guildId=GUILD_ID");
        Log($"Try: http://localhost:{port}/api/vtc/link/consume?guildId=GUILD_ID&code=XXXXXX&driver=BamBam");

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("🔥 Listener exception:");
                Log(ex.ToString());
                break;
            }

            _ = Task.Run(() => HandleHttp(ctx));
        }

        try { listener.Stop(); } catch { }
    }

    private static bool TryGetGuildId(HttpListenerRequest req, out ulong guildId)
    {
        guildId = 0;

        // Prefer query string: ?guildId=123
        var q = (req.QueryString["guildId"] ?? req.QueryString["guild"] ?? req.QueryString["serverId"] ?? "").Trim();
        if (ulong.TryParse(q, out var gid) && gid > 0)
        {
            guildId = gid;
            return true;
        }

        // Optional: header support: X-Guild-Id
        var h = (req.Headers["X-Guild-Id"] ?? "").Trim();
        if (ulong.TryParse(h, out gid) && gid > 0)
        {
            guildId = gid;
            return true;
        }

        return false;
    }

    private static async Task HandleHttp(HttpListenerContext ctx)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var rawPath = ctx.Request.Url?.AbsolutePath ?? "/";
        var path = rawPath.Trim('/').ToLowerInvariant();

        var accept = ctx.Request.Headers["Accept"] ?? "";
        var ua = ctx.Request.Headers["User-Agent"] ?? "";
        Log($"[HTTP] {ctx.Request.HttpMethod} {rawPath}  Accept='{accept}'  UA='{ua}'");

        try
        {
            if (path == "" || path == "api" || path == "docs")
            {
                await WriteJson(ctx, 200, new
                {
                    ok = true,
                    service = "OverWatchELD.VtcBot (GLOBAL / multi-VTC per guild + LINK + LINKED-ONLY ROSTER + PERSIST)",
                    traceId,
                    utc = DateTimeOffset.UtcNow,
                    guildCount = _client?.Guilds.Count ?? 0,
                    endpoints = new[]
                    {
                        "/health",
                        "/api/vtc/servers",
                        "/api/vtc/name?guildId=GUILD_ID",
                        "/api/vtc/roster?guildId=GUILD_ID (linked-only)",
                        "/api/vtc/link/pending?guildId=GUILD_ID",
                        "/api/vtc/link/consume?guildId=GUILD_ID&code=XXXXXX&driver=Name"
                    }
                });
                return;
            }

            if (path == "health" || path == "api/health" || path == "api/vtc/health")
            {
                await WriteJson(ctx, 200, new
                {
                    ok = true,
                    service = "OverWatchELD.VtcBot",
                    traceId,
                    utc = DateTimeOffset.UtcNow,
                    baseDirectory = AppContext.BaseDirectory,
                    discord = new
                    {
                        started = _client != null,
                        state = _client?.ConnectionState.ToString() ?? "NotStarted",
                        botUser = _client?.CurrentUser?.Username,
                        guildCount = _client?.Guilds.Count ?? 0
                    },
                    linkCodesInMemory = _linkCodes.Count,
                    lastPresence = _lastPresence
                });
                return;
            }

            if (path == "api/vtc/servers" || path == "api/servers" || path == "servers")
            {
                var servers = GetServersSafe();

                var list = servers.Select(s => (object)new
                {
                    id = s.id,
                    name = s.name,
                    label = s.name,
                    value = s.id,
                    text = s.name
                }).ToArray();

                await WriteJson(ctx, 200, new
                {
                    traceId,
                    count = servers.Length,
                    servers = list,
                    guilds = list,
                    items = list,
                    data = list
                });
                return;
            }

            // Everything below this requires a guildId
            if (!TryGetGuildId(ctx.Request, out var guildId))
            {
                await WriteJson(ctx, 400, new
                {
                    error = "MissingGuildId",
                    traceId,
                    hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id)"
                });
                return;
            }

            if (path == "api/vtc/name")
            {
                var name = ResolveVtcName(guildId);

                var wantsJson =
                    accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
                    accept.Contains("json", StringComparison.OrdinalIgnoreCase);

                if (!wantsJson)
                {
                    await WriteText(ctx, 200, name);
                    return;
                }

                await WriteJson(ctx, 200, new
                {
                    traceId,
                    guildId = guildId.ToString(),
                    name,
                    vtcName = name,
                    serverName = name,
                    displayName = name
                });
                return;
            }

            if (path == "api/vtc/presence")
            {
                if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJson(ctx, 405, new { error = "MethodNotAllowed", traceId, allowed = "POST" });
                    return;
                }

                var body = await ReadBodyAsync(ctx.Request);
                PresencePing? ping = null;

                try
                {
                    if (!string.IsNullOrWhiteSpace(body))
                        ping = JsonSerializer.Deserialize<PresencePing>(body, JsonReadOpts);
                }
                catch { }

                _lastPresence = ping ?? new PresencePing
                {
                    ReceivedUtc = DateTimeOffset.UtcNow,
                    Raw = Truncate(body, 2000)
                };

                await WriteJson(ctx, 200, new
                {
                    traceId,
                    ok = true,
                    guildId = guildId.ToString(),
                    receivedUtc = _lastPresence.ReceivedUtc
                });
                return;
            }

            if (path == "api/vtc/link/pending")
            {
                var list = _linkCodes.Values
                    .Where(x => x.GuildId == guildId)
                    .OrderByDescending(x => x.CreatedUtc)
                    .Select(x => new
                    {
                        code = x.Code,
                        guildId = x.GuildId.ToString(),
                        discordUserId = x.DiscordUserId.ToString(),
                        discordUserName = x.DiscordUserName,
                        createdUtc = x.CreatedUtc
                    })
                    .ToArray();

                await WriteJson(ctx, 200, new
                {
                    traceId,
                    guildId = guildId.ToString(),
                    count = list.Length,
                    codes = list
                });
                return;
            }

            // ✅ Roster (linked-only) per guild
            if (path == "api/vtc/roster" ||
                path == "api/roster" ||
                path == "roster" ||
                path == "bot/roster" ||
                path == "api/bot/roster")
            {
                var roster = GetLinkedRoster(guildId);
                await WriteJson(ctx, 200, new
                {
                    traceId,
                    guildId = guildId.ToString(),
                    count = roster.Length,
                    drivers = roster
                });
                return;
            }

            // ✅ Link consume endpoint (ELD redeems the code)
            // GET /api/vtc/link/consume?guildId=...&code=ABC123&driver=BamBam
            if (path == "api/vtc/link/consume")
            {
                var code = NormalizeCode(ctx.Request.QueryString["code"] ?? "");
                var driver = (ctx.Request.QueryString["driver"] ?? "").Trim();
                _ = NormalizeDriverKey(driver); // normalize for cleanliness

                if (string.IsNullOrWhiteSpace(code))
                {
                    await WriteJson(ctx, 400, new { error = "MissingCode", traceId, guildId = guildId.ToString() });
                    return;
                }

                if (!_linkCodes.TryRemove(code, out var rec))
                {
                    await WriteJson(ctx, 404, new { error = "CodeNotFound", traceId, guildId = guildId.ToString(), code });
                    return;
                }

                // Ensure the code belongs to this guild
                if (rec.GuildId != guildId)
                {
                    // Put it back so the correct guild can redeem it
                    _linkCodes[code] = rec;

                    await WriteJson(ctx, 409, new
                    {
                        error = "CodeGuildMismatch",
                        traceId,
                        requestedGuildId = guildId.ToString(),
                        codeGuildId = rec.GuildId.ToString()
                    });
                    return;
                }

                var resolvedName = TryResolveDiscordName(guildId, rec.DiscordUserId) ?? rec.DiscordUserName;

                var effectiveDriverName = !string.IsNullOrWhiteSpace(driver) ? driver : _cfg.DefaultDriverName;

                UpsertLinkedDriverFromDiscord(
                    guildId: guildId,
                    discordUserId: rec.DiscordUserId,
                    fallbackTag: rec.DiscordUserName,
                    confirmed: true,
                    driverName: effectiveDriverName
                );

                Log($"✅ LINK CONSUMED+SAVED: guild={guildId} discord:{rec.DiscordUserId} confirmed. driver='{effectiveDriverName}' name='{resolvedName}'");

                await WriteJson(ctx, 200, new
                {
                    ok = true,
                    traceId,
                    guildId = guildId.ToString(),
                    code,
                    driver = driver,
                    discordUserId = rec.DiscordUserId.ToString(),
                    discordUserName = rec.DiscordUserName,
                    resolvedDiscordName = resolvedName,
                    createdUtc = rec.CreatedUtc
                });
                return;
            }

            await WriteJson(ctx, 404, new { error = "NotFound", traceId, requested = rawPath });
        }
        catch (Exception ex)
        {
            Log($"🔥 HTTP handler failed. TraceId={traceId} Path={rawPath}");
            Log(ex.ToString());

            await WriteJson(ctx, 500, new
            {
                error = "InternalServerError",
                traceId,
                requested = rawPath,
                exception = ex.GetType().FullName,
                message = ex.Message
            });
        }
    }

    // ✅ Per-guild name resolution (each Discord server = its own VTC)
    private static string ResolveVtcName(ulong guildId)
    {
        // Optional override (global) if you ever want it:
        if (!string.IsNullOrWhiteSpace(_cfg.VtcName))
            return _cfg.VtcName!;

        try
        {
            var g = _client?.GetGuild(guildId);
            if (g != null && !string.IsNullOrWhiteSpace(g.Name))
                return g.Name;
        }
        catch { }

        return "OverWatch ELD";
    }

    private static (string id, string name)[] GetServersSafe()
    {
        try
        {
            if (_client == null) return Array.Empty<(string id, string name)>();
            if (_client.Guilds == null || _client.Guilds.Count == 0) return Array.Empty<(string id, string name)>();

            return _client.Guilds
                .OrderBy(g => g.Name)
                .Select(g => (g.Id.ToString(), g.Name))
                .ToArray();
        }
        catch
        {
            return Array.Empty<(string id, string name)>();
        }
    }

    private static object[] GetLinkedRoster(ulong guildId)
    {
        try
        {
            var store = GetGuildStore(guildId);
            if (store.Count == 0) return Array.Empty<object>();

            int rowId = 0;

            var list = store.Values
                .OrderBy(x => x.DiscordName ?? x.DriverName ?? x.DriverKey ?? "")
                .Select(x =>
                {
                    rowId++;

                    var uid = x.DiscordUserId.ToString();

                    var resolved = TryResolveDiscordName(guildId, x.DiscordUserId);
                    var discordDisplay = !string.IsNullOrWhiteSpace(resolved)
                        ? resolved
                        : (x.DiscordName ?? "");

                    if (string.IsNullOrWhiteSpace(discordDisplay))
                        discordDisplay = $"DiscordUser-{uid}";

                    return (object)new
                    {
                        id = rowId,
                        name = discordDisplay,
                        driverName = x.DriverName ?? "",
                        confirmed = x.Confirmed,
                        linkedDiscordId = uid,
                        discordUserId = uid,
                        discordName = discordDisplay,
                        linkedUtc = x.LinkedUtc
                    };
                })
                .ToArray();

            return list;
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static string? TryResolveDiscordName(ulong guildId, ulong userId)
    {
        try
        {
            if (_client == null) return null;

            var g = _client.GetGuild(guildId);
            if (g != null)
            {
                var gu = g.GetUser(userId);
                if (gu != null)
                {
                    var dn = (gu.DisplayName ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(dn)) return dn;

                    var un = (gu.Username ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(un)) return un;
                }
            }

            var u2 = _client.GetUser(userId);
            if (u2 != null)
            {
                var un = (u2.Username ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(un)) return un;
            }
        }
        catch { }

        return null;
    }

    // ---------------- Persistence (per guild) ----------------
    private static void LoadLinkedDriversFromDisk(ulong guildId)
    {
        try
        {
            var path = LinkedDriversPathFor(guildId);
            if (!File.Exists(path))
            {
                Log($"(guild {guildId}) linked drivers file not found: {path}");
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;

            var list = JsonSerializer.Deserialize<List<LinkedDriverRecord>>(json, JsonReadOpts);
            if (list == null || list.Count == 0) return;

            var store = GetGuildStore(guildId);
            store.Clear();

            foreach (var rec in list)
            {
                var key = (rec.DriverKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key))
                    key = MakeDiscordKey(rec.DiscordUserId);

                rec.GuildId = guildId;
                rec.DriverKey = key;
                store[key] = rec;
            }

            Log($"✅ Loaded linked drivers for guild {guildId}: {store.Count} record(s)");
        }
        catch (Exception ex)
        {
            Log($"⚠️ Failed to load linked drivers for guild {guildId}:");
            Log(ex.Message);
        }
    }

    private static void SaveLinkedDriversToDisk(ulong guildId)
    {
        try
        {
            var store = GetGuildStore(guildId);
            var path = LinkedDriversPathFor(guildId);

            var list = store.Values
                .OrderBy(x => x.DiscordName ?? x.DriverName ?? x.DriverKey ?? "")
                .ToList();

            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log($"⚠️ Failed to save linked drivers for guild {guildId}:");
            Log(ex.Message);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        try
        {
            if (!req.HasEntityBody) return "";
            using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await sr.ReadToEndAsync();
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max);
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentEncoding = Encoding.UTF8;
        ctx.Response.ContentLength64 = bytes.Length;

        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static async Task WriteText(HttpListenerContext ctx, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentEncoding = Encoding.UTF8;
        ctx.Response.ContentLength64 = bytes.Length;

        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    // ---------------- Models ----------------
    private sealed class PresencePing
    {
        public DateTimeOffset ReceivedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string? DriverName { get; set; }
        public string? Status { get; set; }
        public string? Truck { get; set; }
        public string? Raw { get; set; }
    }

    private sealed class LinkRecord
    {
        public string Code { get; set; } = "";
        public ulong GuildId { get; set; } // ✅ ties the code to a server/VTC
        public ulong DiscordUserId { get; set; }
        public string DiscordUserName { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class LinkedDriverRecord
    {
        public ulong GuildId { get; set; } // ✅ stored for clarity
        public string? DriverKey { get; set; }
        public string? DriverName { get; set; }
        public ulong DiscordUserId { get; set; }
        public string? DiscordName { get; set; }
        public DateTimeOffset LinkedUtc { get; set; }
        public bool Confirmed { get; set; }
    }

    private sealed class BotConfig
    {
        public string? BotToken { get; set; }
        public int Port { get; set; } = 8080;
        public string? VtcName { get; set; }
        public string? DefaultDriverName { get; set; }

        public static BotConfig LoadOrDefault()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (!File.Exists(path)) return new BotConfig();

                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<BotConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (cfg == null) return new BotConfig();
                if (cfg.Port <= 0) cfg.Port = 8080;
                return cfg;
            }
            catch
            {
                return new BotConfig();
            }
        }
    }
}

