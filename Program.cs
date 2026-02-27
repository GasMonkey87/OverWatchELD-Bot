// ✅ Per-guild persistence: linked_drivers_<guildId>.json
// ✅ Per-guild VTC name resolution: /api/vtc/name?guildId=...
// ✅ Link codes are tied to a guild; DMs are blocked for linking
// ✅ Railway env overrides: DISCORD_TOKEN/BOT_TOKEN + PORT + optional VTC_NAME/DEFAULT_DRIVER_NAME
// ✅ Railway env overrides: DISCORD_TOKEN/BOT_TOKEN + PORT + DEFAULT_GUILD_ID + optional VTC_NAME/DEFAULT_DRIVER_NAME
// ✅ Backwards-friendly: /api/vtc/servers still works without guildId
// ✅ NEW: If guildId is missing, bot auto-uses DEFAULT_GUILD_ID (or auto-uses the only guild)

// NOTE: This file keeps your existing endpoints/logic, only adds the "default guild" fallback.

using System;
using System.Collections.Concurrent;
@@ -62,7 +65,7 @@ public static async Task Main(string[] args)
};

// ------------------------------------------------------------
        // ✅ Railway/Host ENV overrides (DISCORD_TOKEN + PORT etc.)
        // ✅ Railway/Host ENV overrides (DISCORD_TOKEN + PORT + DEFAULT_GUILD_ID etc.)
// ------------------------------------------------------------
ApplyEnvironmentOverrides();

@@ -74,7 +77,7 @@ public static async Task Main(string[] args)
if (_cfg.Port <= 0) _cfg.Port = 8080;

Log("Starting OverWatchELD.VtcBot (GLOBAL BOT / multi-VTC per guild + LINK + LINKED-ONLY ROSTER + PERSIST)...");
        Log($"Config: Port={_cfg.Port}  Token={(string.IsNullOrWhiteSpace(_cfg.BotToken) ? "MISSING" : "OK")}");
        Log($"Config: Port={_cfg.Port}  Token={(string.IsNullOrWhiteSpace(_cfg.BotToken) ? "MISSING" : "OK")}  DefaultGuildId={(string.IsNullOrWhiteSpace(_cfg.DefaultGuildId) ? "(none)" : _cfg.DefaultGuildId)}");

// Start Discord (optional)
if (!string.IsNullOrWhiteSpace(_cfg.BotToken))
@@ -206,6 +209,14 @@ private static void ApplyEnvironmentOverrides()
Log($"✅ PORT loaded from environment: {_cfg.Port}");
}

            // ✅ DEFAULT_GUILD_ID (Railway variable): used when guildId missing from requests
            var envDefaultGuild = Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID");
            if (!string.IsNullOrWhiteSpace(envDefaultGuild))
            {
                _cfg.DefaultGuildId = envDefaultGuild.Trim();
                Log($"✅ DEFAULT_GUILD_ID loaded from environment: {_cfg.DefaultGuildId}");
            }

// Optional (not required now)
var envVtcName = Environment.GetEnvironmentVariable("VTC_NAME");
if (!string.IsNullOrWhiteSpace(envVtcName))
@@ -280,11 +291,9 @@ await msg.Channel.SendMessageAsync(
{
await msg.Channel.SendMessageAsync(
"OverWatch ELD VTC Bot is online (GLOBAL bot; each Discord server = its own VTC).\n" +
                    $"Health:   http://localhost:{_cfg.Port}/health\n" +
                    $"Servers:  http://localhost:{_cfg.Port}/api/vtc/servers\n" +
                    "Tip: Your ELD must call APIs with a guildId, e.g.\n" +
                    $"/api/vtc/roster?guildId=YOUR_SERVER_ID\n" +
                    $"/api/vtc/name?guildId=YOUR_SERVER_ID\n"
                    "Health:   /health\n" +
                    "Servers:  /api/vtc/servers\n" +
                    "Tip: Most APIs accept guildId, but the bot can also fallback to DEFAULT_GUILD_ID.\n"
);
}
}
@@ -390,12 +399,12 @@ private static void UpsertLinkedDriverFromDiscord(
// ------------------------------------------------------------
private static async Task RunHttpApiAsync(int port)
{
        // ✅ Railway-safe binding: listen on all interfaces (NOT localhost)
var prefixes = new[]
{
    // ✅ Railway-safe: listen on all interfaces (NOT localhost)
    $"http://0.0.0.0:{port}/",
    $"http://*:{port}/"
};
        {
            $"http://0.0.0.0:{port}/",
            $"http://*:{port}/"
        };

HttpListener? listener = null;
Exception? last = null;
@@ -415,25 +424,23 @@ private static async Task RunHttpApiAsync(int port)
last = ex;
try { listener?.Close(); } catch { }
listener = null;
                Log($"⚠️ Failed to bind {pre}: {ex.Message}");
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
        Log($"✅ Health: /health");
        Log($"✅ Servers: /api/vtc/servers");
        Log($"✅ Name: /api/vtc/name (guildId optional if DEFAULT_GUILD_ID set)");
        Log($"✅ Roster: /api/vtc/roster (guildId optional if DEFAULT_GUILD_ID set)");
        Log($"✅ Link Pending: /api/vtc/link/pending (guildId optional if DEFAULT_GUILD_ID set)");
        Log($"✅ Link Consume: /api/vtc/link/consume?code=XXXXXX&driver=Name (guildId optional if DEFAULT_GUILD_ID set)");

while (listener.IsListening)
{
@@ -459,26 +466,46 @@ private static async Task RunHttpApiAsync(int port)
try { listener.Stop(); } catch { }
}

    // ✅ Step C: guildId resolution with fallbacks
private static bool TryGetGuildId(HttpListenerRequest req, out ulong guildId)
{
guildId = 0;

        // Prefer query string: ?guildId=123
        // 1) Prefer query string: ?guildId=123
var q = (req.QueryString["guildId"] ?? req.QueryString["guild"] ?? req.QueryString["serverId"] ?? "").Trim();
if (ulong.TryParse(q, out var gid) && gid > 0)
{
guildId = gid;
return true;
}

        // Optional: header support: X-Guild-Id
        // 2) Optional: header support: X-Guild-Id
var h = (req.Headers["X-Guild-Id"] ?? "").Trim();
if (ulong.TryParse(h, out gid) && gid > 0)
{
guildId = gid;
return true;
}

        // 3) ✅ DEFAULT_GUILD_ID fallback (Railway variable)
        var def = (_cfg.DefaultGuildId ?? "").Trim();
        if (ulong.TryParse(def, out gid) && gid > 0)
        {
            guildId = gid;
            return true;
        }

        // 4) ✅ If bot is only in ONE server, auto-use it
        try
        {
            if (_client != null && _client.Guilds.Count == 1)
            {
                guildId = _client.Guilds.First().Id;
                return true;
            }
        }
        catch { }

return false;
}

@@ -503,14 +530,15 @@ private static async Task HandleHttp(HttpListenerContext ctx)
traceId,
utc = DateTimeOffset.UtcNow,
guildCount = _client?.Guilds.Count ?? 0,
                    defaultGuildId = _cfg.DefaultGuildId,
endpoints = new[]
{
"/health",
"/api/vtc/servers",
                        "/api/vtc/name?guildId=GUILD_ID",
                        "/api/vtc/roster?guildId=GUILD_ID (linked-only)",
                        "/api/vtc/link/pending?guildId=GUILD_ID",
                        "/api/vtc/link/consume?guildId=GUILD_ID&code=XXXXXX&driver=Name"
                        "/api/vtc/name (guildId optional if DEFAULT_GUILD_ID set)",
                        "/api/vtc/roster (guildId optional if DEFAULT_GUILD_ID set)",
                        "/api/vtc/link/pending (guildId optional if DEFAULT_GUILD_ID set)",
                        "/api/vtc/link/consume?code=XXXXXX&driver=Name (guildId optional if DEFAULT_GUILD_ID set)"
}
});
return;
@@ -532,6 +560,7 @@ private static async Task HandleHttp(HttpListenerContext ctx)
botUser = _client?.CurrentUser?.Username,
guildCount = _client?.Guilds.Count ?? 0
},
                    defaultGuildId = _cfg.DefaultGuildId,
linkCodesInMemory = _linkCodes.Count,
lastPresence = _lastPresence
});
@@ -563,14 +592,14 @@ private static async Task HandleHttp(HttpListenerContext ctx)
return;
}

            // Everything below this requires a guildId
            // Everything below this requires a guildId (but we now have fallbacks)
if (!TryGetGuildId(ctx.Request, out var guildId))
{
await WriteJson(ctx, 400, new
{
error = "MissingGuildId",
traceId,
                    hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id)"
                    hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
});
return;
}
@@ -679,7 +708,7 @@ private static async Task HandleHttp(HttpListenerContext ctx)
}

// ✅ Link consume endpoint (ELD redeems the code)
            // GET /api/vtc/link/consume?guildId=...&code=ABC123&driver=BamBam
            // GET /api/vtc/link/consume?code=ABC123&driver=BamBam   (guildId optional if DEFAULT_GUILD_ID set)
if (path == "api/vtc/link/consume")
{
var code = NormalizeCode(ctx.Request.QueryString["code"] ?? "");
@@ -1022,6 +1051,9 @@ private sealed class BotConfig
public string? VtcName { get; set; }
public string? DefaultDriverName { get; set; }

        // ✅ Step A: Default Guild fallback (Railway variable DEFAULT_GUILD_ID)
        public string? DefaultGuildId { get; set; }

public static BotConfig LoadOrDefault()
{
try
@@ -1046,4 +1078,3 @@ public static BotConfig LoadOrDefault()
}
}
}
