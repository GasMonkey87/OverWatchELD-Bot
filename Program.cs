using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// -----------------------------------------------------------------------------
// OverWatchELD VTC Bot (Railway)
// - Discord client + lightweight HTTP API
// - Proxies ELD messaging to the Hub/SaaS
// - Provides VTC metadata endpoints used by the WPF ELD UI:
//      GET  /api/vtc/servers
//      GET  /api/vtc/name?guildId=
//      GET  /api/vtc/roster?guildId=
//      GET  /api/vtc/announcements?guildId=
// - Stores simple driver links via "!link CODE" (so roster shows driver names)
// - Captures announcements from a channel named "announcements" (or env ANNOUNCEMENTS_CHANNEL_ID)
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds |
                    GatewayIntents.GuildMessages |
                    GatewayIntents.MessageContent
}));

builder.Services.AddHttpClient();

var app = builder.Build();

// ------------------------- Config -------------------------

string NormalizeBaseUrl(string? url)
{
    url = (url ?? "").Trim();
    if (string.IsNullOrWhiteSpace(url)) return "";
    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        url = "https://" + url;
    return url.TrimEnd('/');
}

string? GetGuildIdOrDefault(HttpContext ctx)
{
    var gid = (ctx.Request.Query["guildId"].ToString() ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(gid)) return gid;

    gid = (ctx.Request.Headers["X-Guild-Id"].ToString() ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(gid)) return gid;

    gid = defaultGuildId;
    return string.IsNullOrWhiteSpace(gid) ? null : gid;
}

var discord = app.Services.GetRequiredService<DiscordSocketClient>();

var discordToken = (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "").Trim();
var defaultGuildId = (Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "").Trim();
var hubBaseUrl = NormalizeBaseUrl(Environment.GetEnvironmentVariable("HUB_BASE_URL"));
if (string.IsNullOrWhiteSpace(hubBaseUrl))
    hubBaseUrl = NormalizeBaseUrl(Environment.GetEnvironmentVariable("SAAS_BASE"));

var announcementsChannelId = (Environment.GetEnvironmentVariable("ANNOUNCEMENTS_CHANNEL_ID") ?? "").Trim();

// Persisted in Railway container FS (best-effort)
var linkedDriversPath = Path.Combine(AppContext.BaseDirectory, "linked_drivers.json");

var jsonRead = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var jsonWrite = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

// ------------------------- Stores -------------------------

var linkedDriversLock = new object();
List<LinkedDriver> LoadLinked()
{
    lock (linkedDriversLock)
    {
        try
        {
            if (!File.Exists(linkedDriversPath)) return new List<LinkedDriver>();
            return JsonSerializer.Deserialize<List<LinkedDriver>>(File.ReadAllText(linkedDriversPath, Encoding.UTF8), jsonRead)
                   ?? new List<LinkedDriver>();
        }
        catch { return new List<LinkedDriver>(); }
    }
}

void SaveLinked(List<LinkedDriver> list)
{
    lock (linkedDriversLock)
    {
        try
        {
            File.WriteAllText(linkedDriversPath, JsonSerializer.Serialize(list, jsonWrite), Encoding.UTF8);
        }
        catch { }
    }
}

void UpsertLinked(LinkedDriver d)
{
    var all = LoadLinked();
    var i = all.FindIndex(x => x.GuildId == d.GuildId && x.DiscordUserId == d.DiscordUserId);
    if (i >= 0) all[i] = d; else all.Add(d);
    SaveLinked(all);
}

var announcements = new ConcurrentDictionary<string, List<Announcement>>();
void AddAnnouncement(Announcement a)
{
    var list = announcements.GetOrAdd(a.GuildId, _ => new List<Announcement>());
    lock (list)
    {
        list.Insert(0, a);
        if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
    }
}

List<Announcement> GetAnnouncements(string guildId)
{
    if (!announcements.TryGetValue(guildId, out var list)) return new List<Announcement>();
    lock (list) return list.ToList();
}

DateTimeOffset? LatestAnnouncementUtc(string guildId)
{
    var items = GetAnnouncements(guildId);
    return items.Count == 0 ? null : items[0].CreatedUtc;
}

// ------------------------- Discord wiring -------------------------

discord.Log += (m) =>
{
    Console.WriteLine(m.ToString());
    return Task.CompletedTask;
};

discord.Ready += () =>
{
    Console.WriteLine("✅ Discord bot ready");
    return Task.CompletedTask;
};

discord.MessageReceived += async (msg) =>
{
    try
    {
        if (msg.Author?.IsBot == true) return;

        // Capture announcements
        if (msg.Channel is SocketTextChannel tc)
        {
            var gid = tc.Guild?.Id.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(gid))
            {
                var isAnn = (!string.IsNullOrWhiteSpace(announcementsChannelId) && tc.Id.ToString() == announcementsChannelId)
                            || (tc.Name?.IndexOf("announce", StringComparison.OrdinalIgnoreCase) >= 0);

                if (isAnn)
                {
                    AddAnnouncement(new Announcement
                    {
                        Id = msg.Id.ToString(),
                        GuildId = gid,
                        Text = msg.Content ?? "",
                        Author = msg.Author?.Username ?? "Announcement",
                        CreatedUtc = msg.Timestamp.UtcDateTime
                    });
                }
            }
        }

        // !link CODE  -> stores mapping for roster display
        var content = (msg.Content ?? "").Trim();
        if (content.StartsWith("!link ", StringComparison.OrdinalIgnoreCase))
        {
            var code = content.Substring(6).Trim();
            if (string.IsNullOrWhiteSpace(code)) return;

            if (msg.Channel is SocketGuildChannel gc)
            {
                var gid = gc.Guild?.Id.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(gid)) return;

                var display = msg.Author?.Username ?? "Driver";
                if (msg.Author is SocketGuildUser gu && !string.IsNullOrWhiteSpace(gu.DisplayName))
                    display = gu.DisplayName;

                UpsertLinked(new LinkedDriver
                {
                    GuildId = gid,
                    DiscordUserId = msg.Author!.Id.ToString(),
                    DiscordName = display,
                    LinkCode = code,
                    LinkedUtc = DateTimeOffset.UtcNow
                });

                await msg.Channel.SendMessageAsync("✅ Linked. You can return to the ELD now.");
            }
        }
    }
    catch { }
};

if (string.IsNullOrWhiteSpace(discordToken))
{
    Console.WriteLine("❌ DISCORD_TOKEN is missing.");
}
else
{
    _ = Task.Run(async () =>
    {
        await discord.LoginAsync(TokenType.Bot, discordToken);
        await discord.StartAsync();
    });
}

// ------------------------- Proxy client (Hub/SaaS) -------------------------

var proxy = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
if (!string.IsNullOrWhiteSpace(hubBaseUrl))
{
    proxy.BaseAddress = new Uri(hubBaseUrl + "/", UriKind.Absolute);
}

// ------------------------- HTTP API (Railway) -------------------------

app.MapGet("/health", () => Results.Json(new { ok = true }));

// ---- VTC endpoints used by the ELD UI ----

app.MapGet("/api/vtc/servers", () =>
{
    var items = discord.Guilds.Select(g => new { id = g.Id.ToString(), name = g.Name }).ToList();
    return Results.Json(new { ok = true, items });
});

app.MapGet("/api/vtc/name", (HttpContext ctx) =>
{
    var gid = GetGuildIdOrDefault(ctx);
    if (string.IsNullOrWhiteSpace(gid))
        return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway." }, statusCode: 400);

    var g = discord.Guilds.FirstOrDefault(x => x.Id.ToString() == gid);
    return Results.Json(new { ok = true, guildId = gid, vtcName = g?.Name ?? "" });
});

app.MapGet("/api/vtc/roster", (HttpContext ctx) =>
{
    var gid = GetGuildIdOrDefault(ctx);
    if (string.IsNullOrWhiteSpace(gid))
        return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway." }, statusCode: 400);

    var g = discord.Guilds.FirstOrDefault(x => x.Id.ToString() == gid);
    var vtcName = g?.Name ?? "";

    var items = LoadLinked()
        .Where(x => x.GuildId == gid)
        .OrderByDescending(x => x.LinkedUtc)
        .Select(x => new { discordUserId = x.DiscordUserId, name = x.DiscordName, linkedUtc = x.LinkedUtc })
        .ToList();

    return Results.Json(new { ok = true, guildId = gid, vtcName, items });
});

app.MapGet("/api/vtc/announcements", (HttpContext ctx) =>
{
    var gid = GetGuildIdOrDefault(ctx);
    if (string.IsNullOrWhiteSpace(gid))
        return Results.Json(new { error = "MissingGuildId", hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway." }, statusCode: 400);

    var items = GetAnnouncements(gid)
        .Select(a => new { id = a.Id, text = a.Text, author = a.Author, createdUtc = a.CreatedUtc })
        .ToList();

    return Results.Json(new { ok = true, guildId = gid, latestUtc = LatestAnnouncementUtc(gid), items });
});

// ---- Message proxy (ELD -> Hub/SaaS) ----

app.MapGet("/api/messages", async (HttpContext ctx) =>
{
    if (proxy.BaseAddress == null)
        return Results.Json(new { error = "ProxyFailed", message = "An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set." }, statusCode: 502);

    // If caller didn't provide guildId, inject DEFAULT_GUILD_ID when available
    var hasGuild = !string.IsNullOrWhiteSpace(ctx.Request.Query["guildId"].ToString())
                   || !string.IsNullOrWhiteSpace(ctx.Request.Headers["X-Guild-Id"].ToString());

    var qs = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
    if (!hasGuild && !string.IsNullOrWhiteSpace(defaultGuildId))
    {
        qs = string.IsNullOrWhiteSpace(qs)
            ? $"?guildId={Uri.EscapeDataString(defaultGuildId)}"
            : (qs + $"&guildId={Uri.EscapeDataString(defaultGuildId)}");
    }

    using var resp = await proxy.GetAsync("/api/messages" + qs);
    var txt = await resp.Content.ReadAsStringAsync();
    return Results.Text(txt, "application/json", statusCode: (int)resp.StatusCode);
});

app.MapPost("/api/messages/send", async (HttpContext ctx) =>
{
    if (proxy.BaseAddress == null)
        return Results.Json(new { error = "ProxyFailed", message = "An invalid request URI was provided. Either the request URI must be an absolute URI or BaseAddress must be set." }, statusCode: 502);

    string body;
    using (var r = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        body = await r.ReadToEndAsync();

    var hasGuild = !string.IsNullOrWhiteSpace(ctx.Request.Query["guildId"].ToString())
                   || !string.IsNullOrWhiteSpace(ctx.Request.Headers["X-Guild-Id"].ToString());

    var qs = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
    if (!hasGuild && !string.IsNullOrWhiteSpace(defaultGuildId))
    {
        qs = string.IsNullOrWhiteSpace(qs)
            ? $"?guildId={Uri.EscapeDataString(defaultGuildId)}"
            : (qs + $"&guildId={Uri.EscapeDataString(defaultGuildId)}");
    }

    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    using var resp = await proxy.PostAsync("/api/messages/send" + qs, content);
    var txt = await resp.Content.ReadAsStringAsync();
    return Results.Text(txt, "application/json", statusCode: (int)resp.StatusCode);
});

// ---- Discord -> ELD inbound (optional) ----

app.MapPost("/api/inbound", async (InboundMessage inbound) =>
{
    // Broadcast to Discord dispatch channel if configured in the future.
    // For now we keep this endpoint so existing callers don't break.
    return Results.Json(new { ok = true });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");

await app.RunAsync();

// ------------------------- DTOs -------------------------

sealed class InboundMessage
{
    public string? GuildId { get; set; }
    public string? DriverName { get; set; }
    public string? Text { get; set; }
    public string? Source { get; set; }
}

sealed class LinkedDriver
{
    public string GuildId { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DiscordName { get; set; } = "";
    public string LinkCode { get; set; } = "";
    public DateTimeOffset LinkedUtc { get; set; }
}

sealed class Announcement
{
    public string Id { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}
