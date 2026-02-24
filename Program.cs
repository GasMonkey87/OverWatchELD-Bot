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
    private static readonly string LinkedDriversPath = Path.Combine(AppContext.BaseDirectory, "linked_drivers.json");

    private static DiscordSocketClient? _client;
    private static BotConfig _cfg = BotConfig.LoadOrDefault();

    // Cached VTC name so /api/vtc/name never returns empty
    private static string _cachedVtcName = "OverWatch ELD";

    // Presence ping (optional)
    private static PresencePing? _lastPresence;

    // ✅ Link codes -> Discord user (in-memory)
    private static readonly ConcurrentDictionary<string, LinkRecord> _linkCodes = new(StringComparer.OrdinalIgnoreCase);

    // ✅ Linked drivers (persisted)
    // Key: driverKey (we use "discord:<id>" until ELD confirms a driver name)
    private static readonly ConcurrentDictionary<string, LinkedDriverRecord> _linkedDrivers = new(StringComparer.OrdinalIgnoreCase);

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

        // ✅ Load persisted links BEFORE starting services
        LoadLinkedDriversFromDisk();

        var cfgPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        Log($"Runtime BaseDirectory: {AppContext.BaseDirectory}");
        Log($"Config path: {cfgPath}");
        Log($"Config exists: {File.Exists(cfgPath)}");
        Log($"Linked drivers path: {LinkedDriversPath}");
        Log($"Linked drivers loaded: {_linkedDrivers.Count}");

        if (_cfg.Port <= 0) _cfg.Port = 8080;
        if (!string.IsNullOrWhiteSpace(_cfg.VtcName))
            _cachedVtcName = _cfg.VtcName!;

        Log("Starting OverWatchELD.VtcBot (baseline / no roles + LINK + LINKED-ONLY ROSTER + PERSIST + ROSTER-NAMES)...");
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

                _client.Ready += async () =>
                {
                    Log("✅ Discord client READY.");
                    Log($"✅ Logged in as: {_client.CurrentUser?.Username}#{_client.CurrentUser?.Discriminator}");
                    Log($"✅ Guilds visible: {_client.Guilds.Count}");

                    try
                    {
                        var g = _client.Guilds.OrderBy(x => x.Name).FirstOrDefault();
                        if (g != null && !string.IsNullOrWhiteSpace(g.Name))
                        {
                            _cachedVtcName = g.Name;
                            Log($"✅ Cached VTC name set to: {_cachedVtcName}");

                            try
                            {
                                await g.DownloadUsersAsync();
                                Log($"✅ DownloadUsersAsync complete. Cached members: {g.Users.Count}");
                            }
                            catch (Exception ex)
                            {
                                Log("⚠️ DownloadUsersAsync failed (name resolving may be limited):");
                                Log(ex.Message);
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

    private static async Task OnMessageReceivedAsync(SocketMessage msg)
    {
        try
        {
            if (msg.Author.IsBot) return;

            var content = (msg.Content ?? "").Trim();

            // Support: !link CODE  (also accept /link CODE or "link CODE")
            if (TryParseLinkCode(content, out var code))
            {
                code = NormalizeCode(code);

                var rec = new LinkRecord
                {
                    Code = code,
                    DiscordUserId = msg.Author.Id,
                    DiscordUserName = $"{msg.Author.Username}#{msg.Author.Discriminator}",
                    CreatedUtc = DateTimeOffset.UtcNow
                };

                _linkCodes[code] = rec;

                // ✅ IMPORTANT FIX:
                // Also create/persist a linked roster entry immediately so roster isn't empty.
                // Driver will be "confirmed" when ELD later calls /api/vtc/link/consume?code=...&driver=...
                UpsertLinkedDriverFromDiscord(rec.DiscordUserId, rec.DiscordUserName, confirmed: false, driverName: _cfg.DefaultDriverName);

                Log($"✅ LINK set: code={code} user={msg.Author.Username}({msg.Author.Id})");
                Log($"✅ Linked roster entry ensured for discord:{msg.Author.Id}");

                await msg.Channel.SendMessageAsync(
                    "✅ Linked code created.\n" +
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
                    "OverWatch ELD VTC Bot is online (baseline mode, no roles + LINK).\n" +
                    $"Health:   http://localhost:{_cfg.Port}/health\n" +
                    $"Name:     http://localhost:{_cfg.Port}/api/vtc/name\n" +
                    $"Servers:  http://localhost:{_cfg.Port}/api/vtc/servers\n" +
                    $"Roster (linked-only): http://localhost:{_cfg.Port}/api/vtc/roster\n" +
                    $"Link consume: http://localhost:{_cfg.Port}/api/vtc/link/consume?code=CODE&driver=NAME\n" +
                    $"Pending codes: http://localhost:{_cfg.Port}/api/vtc/link/pending"
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

    private static void UpsertLinkedDriverFromDiscord(ulong discordUserId, string fallbackTag, bool confirmed, string? driverName)
    {
        try
        {
            var key = MakeDiscordKey(discordUserId);

            var resolved = TryResolveDiscordName(discordUserId);
            var display = !string.IsNullOrWhiteSpace(resolved) ? resolved! : (fallbackTag ?? $"DiscordUser-{discordUserId}");

            _linkedDrivers.AddOrUpdate(
                key,
                _ => new LinkedDriverRecord
                {
                    DriverKey = key,
                    DriverName = string.IsNullOrWhiteSpace(driverName) ? null : driverName,
                    DiscordUserId = discordUserId,
                    DiscordName = display,
                    LinkedUtc = DateTimeOffset.UtcNow,
                    Confirmed = confirmed
                },
                (_, existing) =>
                {
                    existing.DiscordUserId = discordUserId;
                    existing.DiscordName = display;
                    if (!string.IsNullOrWhiteSpace(driverName))
                        existing.DriverName = driverName;
                    if (confirmed)
                        existing.Confirmed = true; // only ever promotes to confirmed
                    return existing;
                }
            );

            SaveLinkedDriversToDisk();
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
            $"http://localhost:{port}/",
            $"http://127.0.0.1:{port}/",
            $"http://+:{port}/"
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
        Log($"Try: http://localhost:{port}/api/vtc/name");
        Log($"Try: http://localhost:{port}/api/vtc/presence");
        Log($"Try: http://localhost:{port}/api/vtc/servers");
        Log($"Try: http://localhost:{port}/api/vtc/roster");
        Log($"Try: http://localhost:{port}/api/vtc/link/pending");
        Log($"Try: http://localhost:{port}/api/vtc/link/consume?code=XXXXXX&driver=BamBam");

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
                    service = "OverWatchELD.VtcBot (baseline / no roles + LINK + LINKED-ONLY ROSTER + PERSIST + ROSTER-NAMES)",
                    traceId,
                    utc = DateTimeOffset.UtcNow,
                    vtcName = ResolveVtcName(),
                    linkedDrivers = _linkedDrivers.Count,
                    pendingCodes = _linkCodes.Count,
                    endpoints = new[]
                    {
                        "/health",
                        "/api/vtc/name",
                        "/api/vtc/presence",
                        "/api/vtc/servers",
                        "/api/vtc/roster (linked-only)",
                        "/api/vtc/link/pending",
                        "/api/vtc/link/consume?code=XXXXXX&driver=Name"
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
                    vtcName = ResolveVtcName(),
                    baseDirectory = AppContext.BaseDirectory,
                    discord = new
                    {
                        started = _client != null,
                        state = _client?.ConnectionState.ToString() ?? "NotStarted",
                        botUser = _client?.CurrentUser?.Username,
                        guildCount = _client?.Guilds.Count ?? 0
                    },
                    linkCodesInMemory = _linkCodes.Count,
                    linkedDriversPersisted = _linkedDrivers.Count,
                    linkedDriversFile = LinkedDriversPath,
                    lastPresence = _lastPresence
                });
                return;
            }

            if (path == "api/vtc/name")
            {
                var name = ResolveVtcName();

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
                    receivedUtc = _lastPresence.ReceivedUtc
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

            // ✅ NEW: pending link codes (debug / visibility)
            if (path == "api/vtc/link/pending")
            {
                var list = _linkCodes.Values
                    .OrderByDescending(x => x.CreatedUtc)
                    .Select(x => new
                    {
                        code = x.Code,
                        discordUserId = x.DiscordUserId.ToString(),
                        discordUserName = x.DiscordUserName,
                        createdUtc = x.CreatedUtc
                    })
                    .ToArray();

                await WriteJson(ctx, 200, new
                {
                    traceId,
                    count = list.Length,
                    codes = list
                });
                return;
            }

            // ✅ Roster (linked-only)
            if (path == "api/vtc/roster" ||
                path == "api/roster" ||
                path == "roster" ||
                path == "bot/roster" ||
                path == "api/bot/roster")
            {
                var roster = GetLinkedRoster();
                await WriteJson(ctx, 200, new
                {
                    traceId,
                    count = roster.Length,
                    drivers = roster
                });
                return;
            }

            // ✅ Link consume endpoint (ELD redeems the code)
            // GET /api/vtc/link/consume?code=ABC123&driver=BamBam
            if (path == "api/vtc/link/consume")
            {
                var code = NormalizeCode(ctx.Request.QueryString["code"] ?? "");
                var driver = (ctx.Request.QueryString["driver"] ?? "").Trim();
                var driverKey = NormalizeDriverKey(driver);

                if (string.IsNullOrWhiteSpace(code))
                {
                    await WriteJson(ctx, 400, new { error = "MissingCode", traceId });
                    return;
                }

                if (!_linkCodes.TryRemove(code, out var rec))
                {
                    await WriteJson(ctx, 404, new { error = "CodeNotFound", traceId, code });
                    return;
                }

                var resolvedName = TryResolveDiscordName(rec.DiscordUserId) ?? rec.DiscordUserName;

                // If ELD supplies a driver name, we can store it.
                // Also mark this link CONFIRMED.
                var effectiveDriverName = !string.IsNullOrWhiteSpace(driver) ? driver : _cfg.DefaultDriverName;

                UpsertLinkedDriverFromDiscord(rec.DiscordUserId, rec.DiscordUserName, confirmed: true, driverName: effectiveDriverName);

                Log($"✅ LINK CONSUMED+SAVED: discord:{rec.DiscordUserId} confirmed. driver='{effectiveDriverName}' name='{resolvedName}'");

                await WriteJson(ctx, 200, new
                {
                    ok = true,
                    traceId,
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

    private static string ResolveVtcName()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.VtcName))
            return _cfg.VtcName!;

        if (!string.IsNullOrWhiteSpace(_cachedVtcName))
            return _cachedVtcName;

        try
        {
            if (_client != null && _client.Guilds.Count > 0)
            {
                var g = _client.Guilds.OrderBy(x => x.Name).FirstOrDefault();
                if (g != null && !string.IsNullOrWhiteSpace(g.Name))
                    return g.Name;
            }
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

    private static object[] GetLinkedRoster()
    {
        try
        {
            if (_linkedDrivers.Count == 0)
                return Array.Empty<object>();

            int rowId = 0;

            var list = _linkedDrivers.Values
                .OrderBy(x => x.DiscordName ?? x.DriverName ?? x.DriverKey ?? "")
                .Select(x =>
                {
                    rowId++;

                    var uid = x.DiscordUserId.ToString();

                    // Prefer live-resolved Discord display name if possible
                    var resolved = TryResolveDiscordName(x.DiscordUserId);
                    var discordDisplay = !string.IsNullOrWhiteSpace(resolved)
                        ? resolved
                        : (x.DiscordName ?? "");

                    if (string.IsNullOrWhiteSpace(discordDisplay))
                        discordDisplay = $"DiscordUser-{uid}";

                    // ✅ "name" is what your ELD roster UI most likely shows.
                    // So set it to the Discord display name.
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

    private static string? TryResolveDiscordName(ulong userId)
    {
        try
        {
            if (_client == null) return null;

            var g = _client.Guilds?.OrderBy(x => x.Name).FirstOrDefault();
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

    // ---------------- Persistence ----------------
    private static void LoadLinkedDriversFromDisk()
    {
        try
        {
            if (!File.Exists(LinkedDriversPath)) return;

            var json = File.ReadAllText(LinkedDriversPath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var list = JsonSerializer.Deserialize<List<LinkedDriverRecord>>(json, JsonReadOpts);
            if (list == null || list.Count == 0) return;

            _linkedDrivers.Clear();

            foreach (var rec in list)
            {
                var key = (rec.DriverKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key))
                    key = MakeDiscordKey(rec.DiscordUserId);

                rec.DriverKey = key;
                _linkedDrivers[key] = rec;
            }
        }
        catch (Exception ex)
        {
            Log("⚠️ Failed to load linked drivers from disk:");
            Log(ex.Message);
        }
    }

    private static void SaveLinkedDriversToDisk()
    {
        try
        {
            var list = _linkedDrivers.Values
                .OrderBy(x => x.DiscordName ?? x.DriverName ?? x.DriverKey ?? "")
                .ToList();

            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(LinkedDriversPath, json);
        }
        catch (Exception ex)
        {
            Log("⚠️ Failed to save linked drivers to disk:");
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
        public ulong DiscordUserId { get; set; }
        public string DiscordUserName { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class LinkedDriverRecord
    {
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