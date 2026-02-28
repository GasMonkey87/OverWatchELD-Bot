// Program.cs ✅ FULL COPY/REPLACE
// SaaS Bot (Discord + HTTP API) that talks to HUB for linking.
// - !link CODE  -> POST {HUB_BASE_URL}/api/link/confirm
// - HTTP: /health, /api/messages, /api/messages/send
// - Multi-guild safe: requires guildId, but supports DEFAULT_GUILD_ID fallback
// - Railway safe: binds 0.0.0.0 and respects PORT
// NOTE: Requires Discord.Net + ASP.NET Core runtime (use mcr.microsoft.com/dotnet/aspnet:8.0)

using System.Net.Http.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace OverWatchELD.VtcBot;

public static class Program
{
    private static DiscordSocketClient? _client;
    private static readonly HttpClient _http = new();

    // In-memory message buckets (simple + reliable for now)
    private static readonly Dictionary<string, List<MessageDto>> _messagesByGuild =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task Main(string[] args)
    {
        var cfg = BotConfig.Load();

        // Railway PORT
        var port = cfg.Port > 0 ? cfg.Port : 8080;

        // ---------------------------
        // Start Web API first
        // ---------------------------
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();

        app.MapGet("/", () => Results.NotFound()); // keep root 404 (normal)
        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            discord = new
            {
                tokenPresent = !string.IsNullOrWhiteSpace(cfg.BotToken),
                connected = _client?.ConnectionState == ConnectionState.Connected,
                botUser = _client?.CurrentUser?.Username ?? "",
                guildCount = _client?.Guilds?.Count ?? 0
            },
            defaultGuildId = cfg.DefaultGuildId ?? ""
        }));

        // GET /api/messages?guildId=123&driverName=Bob
        app.MapGet("/api/messages", (HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, cfg);
            if (guildId is null) return MissingGuildId();

            _messagesByGuild.TryGetValue(guildId, out var list);
            list ??= new List<MessageDto>();

            var driverName = req.Query["driverName"].ToString();
            if (!string.IsNullOrWhiteSpace(driverName))
                list = list.Where(m => string.Equals(m.DriverName, driverName, StringComparison.OrdinalIgnoreCase)).ToList();

            var items = list.OrderByDescending(x => x.CreatedUtc).Take(200).ToList();
            return Results.Ok(new { ok = true, guildId, items });
        });

        // POST /api/messages/send?guildId=123  body: { driverName, text, source }
        app.MapPost("/api/messages/send", async (HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, cfg);
            if (guildId is null) return MissingGuildId();

            var body = await req.ReadFromJsonAsync<SendMessageReq>();
            if (body is null || string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "EmptyMessage" });

            var msg = new MessageDto
            {
                Id = Guid.NewGuid().ToString("N"),
                GuildId = guildId,
                DriverName = (body.DriverName ?? "").Trim(),
                Text = body.Text.Trim(),
                Source = string.IsNullOrWhiteSpace(body.Source) ? "eld" : body.Source.Trim(),
                CreatedUtc = DateTimeOffset.UtcNow
            };

            var bucket = GetBucket(guildId);
            lock (bucket) bucket.Add(msg);

            // OPTIONAL: if connected to Discord, also post to a channel (dispatchChannelId)
            if (_client?.ConnectionState == ConnectionState.Connected && ulong.TryParse(cfg.DispatchChannelId, out var chanId) && chanId > 0)
            {
                try
                {
                    if (_client.GetChannel(chanId) is IMessageChannel chan)
                    {
                        var who = string.IsNullOrWhiteSpace(msg.DriverName) ? "Driver" : msg.DriverName;
                        await chan.SendMessageAsync($"📨 **{who}**: {msg.Text}");
                    }
                }
                catch { /* never break API */ }
            }

            return Results.Ok(new { ok = true, id = msg.Id });
        });

        // Start web
        var webTask = app.RunAsync();

        // ---------------------------
        // Start Discord
        // ---------------------------
        if (!string.IsNullOrWhiteSpace(cfg.BotToken))
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents =
                    GatewayIntents.Guilds |
                    GatewayIntents.GuildMessages |
                    GatewayIntents.MessageContent
            });

            _client.Log += m =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m.Severity} {m.Source}: {m.Message} {m.Exception}");
                return Task.CompletedTask;
            };

            _client.MessageReceived += msg => OnMessageReceived(msg, cfg);

            await _client.LoginAsync(TokenType.Bot, cfg.BotToken);
            await _client.StartAsync();
        }

        await webTask;
    }

    private static async Task OnMessageReceived(SocketMessage raw, BotConfig cfg)
    {
        // ignore bots + DMs
        if (raw.Author.IsBot) return;
        if (raw.Channel is not SocketGuildChannel gchan) return;

        var text = raw.Content?.Trim() ?? "";
        if (text.Length == 0) return;

        // !link CODE
        if (text.StartsWith("!link", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await raw.Channel.SendMessageAsync("Usage: `!link CODE` (CODE comes from the ELD Support/Link screen)");
                return;
            }

            var code = parts[1].Trim().ToUpperInvariant();
            var hub = cfg.HubBaseUrl;
            if (string.IsNullOrWhiteSpace(hub))
            {
                await raw.Channel.SendMessageAsync("❌ HUB is not configured. Set Railway variable `HUB_BASE_URL`.");
                return;
            }

            try
            {
                var payload = new ConfirmLinkReq
                {
                    Code = code,
                    GuildId = gchan.Guild.Id.ToString(),
                    GuildName = gchan.Guild.Name,
                    LinkedByUserId = raw.Author.Id.ToString()
                };

                // This is the SaaS “source of truth”
                var url = $"{hub.TrimEnd('/')}/api/link/confirm";
                var resp = await _http.PostAsJsonAsync(url, payload);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    await raw.Channel.SendMessageAsync($"❌ Link failed ({(int)resp.StatusCode}). {body}");
                    return;
                }

                await raw.Channel.SendMessageAsync($"✅ Linked! Code `{code}` is now tied to **{gchan.Guild.Name}**.");
            }
            catch (Exception ex)
            {
                await raw.Channel.SendMessageAsync($"❌ Link error: {ex.Message}");
            }

            return;
        }

        // Optional: store incoming Discord messages into message bucket (for ELD pull)
        // This lets Discord -> ELD show up in ELD messages.
        var gid = gchan.Guild.Id.ToString();
        var bucket = GetBucket(gid);

        var dto = new MessageDto
        {
            Id = Guid.NewGuid().ToString("N"),
            GuildId = gid,
            DriverName = raw.Author.Username,
            Text = text,
            Source = "discord",
            CreatedUtc = DateTimeOffset.UtcNow
        };

        lock (bucket) bucket.Add(dto);
    }

    private static List<MessageDto> GetBucket(string guildId)
    {
        lock (_messagesByGuild)
        {
            if (!_messagesByGuild.TryGetValue(guildId, out var list))
            {
                list = new List<MessageDto>();
                _messagesByGuild[guildId] = list;
            }
            return list;
        }
    }

    private static string? ResolveGuildId(HttpRequest req, BotConfig cfg)
    {
        var q = req.Query["guildId"].ToString();
        if (!string.IsNullOrWhiteSpace(q)) return q.Trim();

        if (req.Headers.TryGetValue("X-Guild-Id", out var h))
        {
            var v = h.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        var def = (cfg.DefaultGuildId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(def)) return def;

        // if only 1 guild, auto-use it
        try
        {
            if (_client != null && _client.Guilds.Count == 1)
                return _client.Guilds.First().Id.ToString();
        }
        catch { }

        return null;
    }

    private static IResult MissingGuildId() => Results.BadRequest(new
    {
        error = "MissingGuildId",
        traceId = Guid.NewGuid().ToString("N"),
        hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
    });
}

file sealed class BotConfig
{
    public int Port { get; set; } = 8080;

    public string BotToken { get; set; } = "";
    public string? DefaultGuildId { get; set; }

    // Where the bot posts ELD messages (optional)
    public string DispatchChannelId { get; set; } = "";

    // SaaS Hub
    public string HubBaseUrl { get; set; } = "";

    public static BotConfig Load()
    {
        var cfg = new BotConfig();

        // PORT
        if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) && p > 0) cfg.Port = p;

        // Token
        cfg.BotToken =
            (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ??
             Environment.GetEnvironmentVariable("BOT_TOKEN") ??
             "").Trim();

        // Hub URL
        cfg.HubBaseUrl = (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? "").Trim();

        // Default guild
        cfg.DefaultGuildId = (Environment.GetEnvironmentVariable("DEFAULT_GUILD_ID") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cfg.DefaultGuildId)) cfg.DefaultGuildId = null;

        // Optional channel
        cfg.DispatchChannelId = (Environment.GetEnvironmentVariable("DISPATCH_CHANNEL_ID") ?? "").Trim();

        return cfg;
    }
}

file sealed class ConfirmLinkReq
{
    public string Code { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string? GuildName { get; set; }
    public string? LinkedByUserId { get; set; }
}

file sealed class SendMessageReq
{
    public string? DriverName { get; set; }
    public string Text { get; set; } = "";
    public string? Source { get; set; }
}

file sealed class MessageDto
{
    public string Id { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}
