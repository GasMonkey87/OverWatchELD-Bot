using System.Collections.Concurrent;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();
app.UseForwardedHeaders();

var messagesByGuild = new ConcurrentDictionary<string, List<MessageDto>>(StringComparer.OrdinalIgnoreCase);

static string? GetGuildId(HttpRequest req)
{
    var q = req.Query["guildId"].ToString();
    if (!string.IsNullOrWhiteSpace(q)) return q.Trim();

    if (req.Headers.TryGetValue("X-Guild-Id", out var h))
    {
        var v = h.ToString();
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
    }

    var env = Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

    return null;
}

static IResult MissingGuildId()
{
    return Results.BadRequest(new
    {
        error = "MissingGuildId",
        traceId = Guid.NewGuid().ToString("N"),
        hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
    });
}

// ✅ Force bind to Railway PORT (prevents “failed to respond”)
var portStr = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out var p) && p > 0)
{
    app.Urls.Clear();
    app.Urls.Add($"http://0.0.0.0:{p}");
}

app.MapGet("/", () => Results.Ok(new
{
    ok = true,
    name = "OverWatchELD Bot API",
    endpoints = new[]
    {
        "/health",
        "/api/messages?guildId=123",
        "/api/messages/send?guildId=123"
    }
}));

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/api/messages", (HttpRequest http, string? driverName) =>
{
    var guildId = GetGuildId(http);
    if (string.IsNullOrWhiteSpace(guildId)) return MissingGuildId();

    messagesByGuild.TryGetValue(guildId, out var list);
    list ??= new List<MessageDto>();

    if (!string.IsNullOrWhiteSpace(driverName))
        list = list.Where(m => string.Equals(m.DriverName, driverName, StringComparison.OrdinalIgnoreCase)).ToList();

    return Results.Ok(new { ok = true, guildId, items = list.OrderByDescending(x => x.CreatedUtc).Take(200) });
});

app.MapPost("/api/messages/send", (HttpRequest http, SendMessageReq req) =>
{
    var guildId = GetGuildId(http);
    if (string.IsNullOrWhiteSpace(guildId)) return MissingGuildId();
    if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "EmptyMessage" });

    var msg = new MessageDto
    {
        Id = Guid.NewGuid().ToString("N"),
        GuildId = guildId,
        DriverName = (req.DriverName ?? "").Trim(),
        Text = req.Text.Trim(),
        Source = (req.Source ?? "eld").Trim(),
        CreatedUtc = DateTimeOffset.UtcNow
    };

    var bucket = messagesByGuild.GetOrAdd(guildId, _ => new List<MessageDto>());
    lock (bucket) bucket.Add(msg);

    return Results.Ok(new { ok = true, id = msg.Id });
});

app.Run();

sealed class SendMessageReq
{
    public string? DriverName { get; set; }
    public string Text { get; set; } = "";
    public string? Source { get; set; }
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
