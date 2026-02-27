using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Railway / proxies
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();
app.UseForwardedHeaders();

// -----------------------------
// In-memory storage (for now)
// Replace with your real DB later
// -----------------------------
var links = new ConcurrentDictionary<string, LinkRecord>(StringComparer.OrdinalIgnoreCase);

// Example message store: per guild -> list of messages
var messagesByGuild = new ConcurrentDictionary<string, List<MessageDto>>(StringComparer.OrdinalIgnoreCase);

// -----------------------------
// Helpers
// -----------------------------
static string? GetGuildId(HttpRequest req)
{
    // 1) query ?guildId=
    var q = req.Query["guildId"].ToString();
    if (!string.IsNullOrWhiteSpace(q)) return q.Trim();

    // 2) header X-Guild-Id
    if (req.Headers.TryGetValue("X-Guild-Id", out var h))
    {
        var v = h.ToString();
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
    }

    // 3) env DEFAULT_GUILD_ID
    var env = Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

    return null;
}

static IResult MissingGuildId(string? traceId = null)
{
    return Results.BadRequest(new
    {
        error = "MissingGuildId",
        traceId = traceId ?? Guid.NewGuid().ToString("N"),
        hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
    });
}

static string NewToken()
{
    Span<byte> b = stackalloc byte[24];
    RandomNumberGenerator.Fill(b);
    return "tok_" + Convert.ToBase64String(b).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

// -----------------------------
// Link flow (SaaS-safe)
// ELD generates code -> bot !link CODE -> ELD claims -> gets guildId + token
// -----------------------------

app.MapPost("/api/link/register", (RegisterLinkReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "InvalidCode" });

    var code = req.Code.Trim().ToUpperInvariant();

    var rec = new LinkRecord
    {
        Code = code,
        CreatedUtc = DateTimeOffset.UtcNow,
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(req.ExpiresMinutes ?? 10, 1, 60)),
        Status = LinkStatus.PendingDiscord,
        DriverName = req.DriverName?.Trim(),
        DeviceName = req.DeviceName?.Trim(),
        DeviceToken = null,
        GuildId = null,
        GuildName = null,
        LinkedByDiscordUserId = null,
    };

    links[code] = rec;

    return Results.Ok(new
    {
        ok = true,
        code,
        expiresUtc = rec.ExpiresUtc
    });
});

app.MapPost("/api/link/confirm", (ConfirmLinkReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "InvalidCode" });

    var code = req.Code.Trim().ToUpperInvariant();

    if (!links.TryGetValue(code, out var rec))
        return Results.NotFound(new { error = "UnknownCode" });

    if (rec.ExpiresUtc <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { error = "ExpiredCode" });

    if (string.IsNullOrWhiteSpace(req.GuildId))
        return Results.BadRequest(new { error = "MissingGuildId" });

    rec.GuildId = req.GuildId.Trim();
    rec.GuildName = req.GuildName?.Trim();
    rec.LinkedByDiscordUserId = req.LinkedByUserId?.Trim();

    // Generate a per-install device token (SaaS-safe; no shared secrets)
    rec.DeviceToken ??= NewToken();

    rec.Status = LinkStatus.Linked;

    // Ensure guild message bucket exists
    messagesByGuild.TryAdd(rec.GuildId, new List<MessageDto>());

    return Results.Ok(new
    {
        ok = true,
        code,
        guildId = rec.GuildId,
        guildName = rec.GuildName,
        status = rec.Status.ToString()
    });
});

app.MapPost("/api/link/claim", (ClaimLinkReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "InvalidCode" });

    var code = req.Code.Trim().ToUpperInvariant();

    if (!links.TryGetValue(code, out var rec))
        return Results.NotFound(new { error = "UnknownCode" });

    if (rec.ExpiresUtc <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { error = "ExpiredCode" });

    if (rec.Status != LinkStatus.Linked || string.IsNullOrWhiteSpace(rec.GuildId))
        return Results.BadRequest(new { error = "NotLinkedYet", hint = "Run !link CODE inside your Discord server first." });

    // Optional: single-use behavior
    if (req.SingleUse ?? true)
    {
        // mark used but keep record for diagnostics
        rec.Status = LinkStatus.Claimed;
    }

    return Results.Ok(new
    {
        ok = true,
        code,
        guildId = rec.GuildId,
        guildName = rec.GuildName ?? "",
        deviceToken = rec.DeviceToken ?? ""
    });
});

// -----------------------------
// Messaging (requires GuildId)
// This is the piece returning MissingGuildId today.
// -----------------------------
app.MapGet("/api/messages", (HttpRequest http, string? driverName) =>
{
    var guildId = GetGuildId(http);
    if (string.IsNullOrWhiteSpace(guildId))
        return MissingGuildId();

    // Minimal response format - adjust to your existing client contract
    messagesByGuild.TryGetValue(guildId, out var list);
    list ??= new List<MessageDto>();

    // Optionally filter by driverName
    if (!string.IsNullOrWhiteSpace(driverName))
        list = list.Where(m => string.Equals(m.DriverName, driverName, StringComparison.OrdinalIgnoreCase)).ToList();

    return Results.Ok(new
    {
        ok = true,
        guildId,
        items = list.OrderByDescending(x => x.CreatedUtc).Take(200)
    });
});

app.MapPost("/api/messages/send", (HttpRequest http, SendMessageReq req) =>
{
    var guildId = GetGuildId(http);
    if (string.IsNullOrWhiteSpace(guildId))
        return MissingGuildId();

    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "EmptyMessage" });

    var msg = new MessageDto
    {
        Id = Guid.NewGuid().ToString("N"),
        GuildId = guildId,
        DriverName = req.DriverName?.Trim() ?? "",
        Text = req.Text.Trim(),
        CreatedUtc = DateTimeOffset.UtcNow,
        Source = req.Source?.Trim() ?? "eld"
    };

    var bucket = messagesByGuild.GetOrAdd(guildId, _ => new List<MessageDto>());
    lock (bucket) bucket.Add(msg);

    return Results.Ok(new { ok = true, id = msg.Id });
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();

// -----------------------------
// Models
// -----------------------------
sealed class RegisterLinkReq
{
    public string Code { get; set; } = "";
    public string? DriverName { get; set; }
    public string? DeviceName { get; set; }
    public int? ExpiresMinutes { get; set; } // default 10
}

sealed class ConfirmLinkReq
{
    public string Code { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string? GuildName { get; set; }
    public string? LinkedByUserId { get; set; }
}

sealed class ClaimLinkReq
{
    public string Code { get; set; } = "";
    public bool? SingleUse { get; set; } // default true
}

sealed class SendMessageReq
{
    public string? DriverName { get; set; }
    public string Text { get; set; } = "";
    public string? Source { get; set; } // "eld" / "discord"
}

sealed class MessageDto
{
    public string Id { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}

enum LinkStatus
{
    PendingDiscord,
    Linked,
    Claimed
}

sealed class LinkRecord
{
    public string Code { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public LinkStatus Status { get; set; }

    public string? DriverName { get; set; }
    public string? DeviceName { get; set; }

    public string? GuildId { get; set; }
    public string? GuildName { get; set; }
    public string? LinkedByDiscordUserId { get; set; }

    public string? DeviceToken { get; set; }
}
