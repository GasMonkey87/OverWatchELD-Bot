using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.HttpOverrides;

internal static class Program
{
    private static DiscordSocketClient? _client;

    // In-memory message buckets by guild id (string)
    private static readonly ConcurrentDictionary<string, List<MessageDto>> _messagesByGuild =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task Main(string[] args)
    {
        // -------- Config --------
        var token = (Env("DISCORD_TOKEN") ?? Env("BOT_TOKEN") ?? "").Trim();
        var defaultGuildId = (Env("DEFAULT_GUILD_ID") ?? "").Trim();

        var port = 8080;
        if (int.TryParse(Env("PORT"), out var p) && p > 0) port = p;

        // -------- Start Discord (optional) --------
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                _client = new DiscordSocketClient(new DiscordSocketConfig
                {
                    GatewayIntents =
                        GatewayIntents.Guilds |
                        GatewayIntents.GuildMessages |
                        GatewayIntents.MessageContent
                });

                _client.Log += msg => { Console.WriteLine(msg.ToString()); return Task.CompletedTask; };

                await _client.LoginAsync(TokenType.Bot, token);
                await _client.StartAsync();
            }
            catch (Exception ex)
            {
                // Don't crash the web API if Discord token is wrong.
                Console.WriteLine("Discord failed to start: " + ex);
            }
        }
        else
        {
            Console.WriteLine("DISCORD_TOKEN/BOT_TOKEN is missing. Discord bot will not connect (API still runs).");
        }

        // -------- Start Web API (Railway) --------
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });

        var app = builder.Build();

        app.UseForwardedHeaders();

        // ✅ Root path returns JSON so it's not confusing (no more 404 at /)
        app.MapGet("/", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD.VtcBot",
            endpoints = new[]
            {
                "/health",
                "/api/messages?guildId=YOUR_GUILD_ID",
                "/api/messages/send?guildId=YOUR_GUILD_ID"
            }
        }));

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            discord = new
            {
                tokenPresent = !string.IsNullOrWhiteSpace(token),
                connected = _client?.ConnectionState == ConnectionState.Connected,
                botUser = _client?.CurrentUser?.Username ?? "",
                guildCount = _client?.Guilds.Count ?? 0
            },
            defaultGuildId = defaultGuildId
        }));

        // GET /api/messages?guildId=...&driverName=...
        app.MapGet("/api/messages", (HttpRequest http, string? driverName) =>
        {
            var guildId = GetGuildId(http, defaultGuildId);
            if (string.IsNullOrWhiteSpace(guildId)) return MissingGuildId();

            _messagesByGuild.TryGetValue(guildId, out var list);
            list ??= new List<MessageDto>();

            IEnumerable<MessageDto> q = list;

            if (!string.IsNullOrWhiteSpace(driverName))
                q = q.Where(m => string.Equals(m.DriverName, driverName, StringComparison.OrdinalIgnoreCase));

            var items = q.OrderByDescending(x => x.CreatedUtc).Take(200).ToList();
            return Results.Ok(new { ok = true, guildId, items });
        });

        // POST /api/messages/send?guildId=...
        // Body: { "driverName": "...", "text": "...", "source": "eld" }
        app.MapPost("/api/messages/send", async (HttpRequest http) =>
        {
            var guildId = GetGuildId(http, defaultGuildId);
            if (string.IsNullOrWhiteSpace(guildId)) return MissingGuildId();

            SendMessageReq? req;
            try
            {
                req = await http.ReadFromJsonAsync<SendMessageReq>();
            }
            catch
            {
                req = null;
            }

            if (req == null) return Results.BadRequest(new { error = "InvalidJson" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "EmptyMessage" });

            var msg = new MessageDto
            {
                Id = Guid.NewGuid().ToString("N"),
                GuildId = guildId,
                DriverName = (req.DriverName ?? "").Trim(),
                Text = req.Text.Trim(),
                Source = string.IsNullOrWhiteSpace(req.Source) ? "eld" : req.Source.Trim(),
                CreatedUtc = DateTimeOffset.UtcNow
            };

            var bucket = _messagesByGuild.GetOrAdd(guildId, _ => new List<MessageDto>());
            lock (bucket) bucket.Add(msg);

            return Results.Ok(new { ok = true, id = msg.Id });
        });

        app.Urls.Clear();
        app.Urls.Add($"http://0.0.0.0:{port}");

        Console.WriteLine($"Listening on 0.0.0.0:{port}");
        await app.RunAsync();
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static string? GetGuildId(HttpRequest req, string defaultGuildId)
    {
        var q = req.Query["guildId"].ToString();
        if (!string.IsNullOrWhiteSpace(q)) return q.Trim();

        if (req.Headers.TryGetValue("X-Guild-Id", out var h))
        {
            var v = h.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        if (!string.IsNullOrWhiteSpace(defaultGuildId)) return defaultGuildId;
        return null;
    }

    private static IResult MissingGuildId()
    {
        return Results.BadRequest(new
        {
            error = "MissingGuildId",
            hint = "Provide ?guildId=YOUR_SERVER_ID (or header X-Guild-Id), or set DEFAULT_GUILD_ID in Railway."
        });
    }
}
