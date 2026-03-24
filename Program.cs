using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Commands;
using OverWatchELD.VtcBot.Routes;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;

namespace OverWatchELD.VtcBot;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static bool _messageHandlerAttached;
    private static bool _slashWired;
    private static bool _slashRegisteredOnce;
    private static volatile bool _discordReady;
    private static readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task Main()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var performanceDir = Path.Combine(dataDir, "performance");
        var guildsDir = Path.Combine(dataDir, "guilds");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(performanceDir);
        Directory.CreateDirectory(guildsDir);

        var services = new BotServices
        {
            ThreadStore = new ThreadMapStore(Path.Combine(dataDir, "thread_map.json")),
            DispatchStore = new DispatchSettingsStore(
                Path.Combine(dataDir, "dispatch_settings.json"),
                JsonReadOpts,
                JsonWriteOpts),
            RosterStore = new VtcRosterStore(
                Path.Combine(dataDir, "vtc_roster.json"),
                JsonReadOpts,
                JsonWriteOpts),
            LinkCodeStore = new LinkCodeStore(
                Path.Combine(dataDir, "link_codes.json"),
                JsonReadOpts,
                JsonWriteOpts),
            LinkedDriversStore = new LinkedDriversStore(
                Path.Combine(dataDir, "linked_drivers.json"),
                JsonReadOpts,
                JsonWriteOpts),
            PerformanceStore = new PerformanceStore(
                performanceDir,
                JsonReadOpts,
                JsonWriteOpts),
            AwardStore = new VtcAwardStore(
                Path.Combine(dataDir, "awards.json"),
                JsonReadOpts,
                JsonWriteOpts),
            DriverAwardStore = new DriverAwardStore(
                Path.Combine(dataDir, "driver_awards.json"),
                JsonReadOpts,
                JsonWriteOpts)
        };

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
                GatewayIntents.GuildMembers |
                GatewayIntents.GuildPresences
        });

        services.Client = _client;

        _client.Ready += async () =>
        {
            _discordReady = true;
            services.DiscordReady = true;
            Console.WriteLine("✅ Discord client READY");

            if (!_slashWired)
            {
                _client.InteractionCreated += inter =>
                    SlashCommandService.HandleInteractionAsync(inter, services.PerformanceStore);

                _client.GuildAvailable += async g =>
                {
                    try { await SlashCommandService.RegisterSlashCommandsForGuildAsync(g); }
                    catch { }
                };

                _slashWired = true;
            }

            if (!_slashRegisteredOnce)
            {
                _slashRegisteredOnce = true;

                foreach (var g in _client.Guilds)
                {
                    try { await SlashCommandService.RegisterSlashCommandsForGuildAsync(g); }
                    catch { }
                }
            }
        };

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {msg.Severity,-7} {msg.Source,-12} {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        };

        if (!_messageHandlerAttached)
        {
            _client.MessageReceived += msg => BotCommandHandler.HandleMessageAsync(msg, services);
            _messageHandlerAttached = true;
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _ = Task.Run(() =>
            PerformancePullService.RunAsync(_client, () => _discordReady, services.PerformanceStore, _http, JsonReadOpts));

        var portStr = (Environment.GetEnvironmentVariable("PORT") ?? "8080").Trim();
        if (!int.TryParse(portStr, out var port)) port = 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("LiveMapCors", policy =>
            {
                policy
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .SetIsOriginAllowed(_ => true);
            });
        });

        var app = builder.Build();

        app.UseCors("LiveMapCors");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/", () =>
        {
            var guildCount = _client?.Guilds.Count ?? 0;
            var uptime = DateTimeOffset.UtcNow - _startedUtc;
            var uptimeText = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
            var statusText = _discordReady ? "ONLINE ✅" : "STARTING ⏳";

            var html =
                "<html>" +
                "<head>" +
                "<title>OverWatchELD Bot</title>" +
                "<style>" +
                "body { font-family: Arial; background:#0f172a; color:#e5e7eb; padding:40px; }" +
                "h1 { color:#22c55e; margin-top:0; }" +
                "a { color:#38bdf8; text-decoration:none; }" +
                "li { margin:8px 0; }" +
                ".box { background:#111827; padding:24px; border-radius:12px; max-width:760px; }" +
                ".grid { display:grid; grid-template-columns:repeat(2,minmax(180px,1fr)); gap:12px; margin:18px 0 24px 0; }" +
                ".card { background:#1f2937; padding:14px; border-radius:10px; }" +
                ".label { font-size:12px; color:#9ca3af; text-transform:uppercase; }" +
                ".value { font-size:22px; font-weight:bold; margin-top:6px; }" +
                "</style>" +
                "</head>" +
                "<body>" +
                "<div class=\"box\">" +
                "<h1>🚛 OverWatchELD.VtcBot</h1>" +
                "<p>GasMonkey / Veterans Logistics system is running.</p>" +
                "<div class=\"grid\">" +
                "<div class=\"card\"><div class=\"label\">Status</div><div class=\"value\">" + statusText + "</div></div>" +
                "<div class=\"card\"><div class=\"label\">Guilds Connected</div><div class=\"value\">" + guildCount + "</div></div>" +
                "<div class=\"card\"><div class=\"label\">Uptime</div><div class=\"value\">" + uptimeText + "</div></div>" +
                "<div class=\"card\"><div class=\"label\">Build</div><div class=\"value\">phase3-dashboard + shared-guild-store</div></div>" +
                "</div>" +
                "<h3>Endpoints</h3>" +
                "<ul>" +
                "<li><a href=\"/health\">/health</a></li>" +
                "<li><a href=\"/build\">/build</a></li>" +
                "<li><a href=\"/api/status\">/api/status</a></li>" +
                "<li><a href=\"/api/vtc/servers\">/api/vtc/servers</a></li>" +
                "<li><a href=\"/api/vtc/name\">/api/vtc/name</a></li>" +
                "<li><a href=\"/api/performance/top\">/api/performance/top</a></li>" +
                "<li><a href=\"/dashboard\">/dashboard</a></li>" +
                "<li><a href=\"/live-map\">/live-map</a></li>" +
                "</ul>" +
                "</div>" +
                "</body>" +
                "</html>";

            return Results.Content(html, "text/html");
        });

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/build", () => Results.Ok(new
        {
            ok = true,
            name = "OverWatchELD.VtcBot",
            version = "phase3-dashboard + shared-guild-store"
        }));

        app.MapGet("/api/status", () =>
        {
            var guildCount = _client?.Guilds.Count ?? 0;
            var uptime = DateTimeOffset.UtcNow - _startedUtc;

            return Results.Ok(new
            {
                ok = true,
                discordReady = _discordReady,
                guilds = guildCount,
                uptimeSeconds = (long)uptime.TotalSeconds,
                startedUtc = _startedUtc.UtcDateTime,
                version = "phase3-dashboard + shared-guild-store"
            });
        });

        // Existing routes
        ApiRoutes.Register(app, services, JsonReadOpts, JsonWriteOpts, _http);
        DashboardRoutes.Register(app);
        AwardRoutes.Register(app, services, JsonReadOpts);

        // New shared guild-scoped JSON routes
        RegisterSharedGuildRoutes(app, guildsDir);

        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        await app.RunAsync();
    }

    private static void RegisterSharedGuildRoutes(WebApplication app, string guildsDir)
    {
        app.MapGet("/api/vtc/shared/{kind}", (string kind, HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var safeKind = NormalizeKind(kind);
            if (string.IsNullOrWhiteSpace(safeKind))
                return Results.BadRequest(new { ok = false, error = "InvalidKind" });

            var path = GetGuildFilePath(guildsDir, guildId, safeKind);
            var json = ReadJsonOrDefault(path, GetDefaultJsonForKind(safeKind));

            return Results.Content(json, "application/json", Encoding.UTF8);
        });

        app.MapPost("/api/vtc/shared/{kind}/save", async (string kind, HttpRequest req) =>
        {
            var safeKind = NormalizeKind(kind);
            if (string.IsNullOrWhiteSpace(safeKind))
                return Results.BadRequest(new { ok = false, error = "InvalidKind" });

            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var raw = (await reader.ReadToEndAsync()).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { ok = false, error = "EmptyBody" });

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch
            {
                return Results.BadRequest(new { ok = false, error = "BadJson" });
            }

            using (doc)
            {
                var root = doc.RootElement;

                var guildId =
                    root.TryGetProperty("guildId", out var gidEl) ? (gidEl.GetString() ?? "").Trim() : "";

                if (string.IsNullOrWhiteSpace(guildId))
                    return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

                var payload =
                    root.TryGetProperty("data", out var dataEl) ? dataEl.GetRawText() : "";

                if (string.IsNullOrWhiteSpace(payload))
                    return Results.BadRequest(new { ok = false, error = "MissingData" });

                var path = GetGuildFilePath(guildsDir, guildId, safeKind);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, FormatJson(payload));

                return Results.Ok(new
                {
                    ok = true,
                    guildId,
                    kind = safeKind,
                    saved = true,
                    path = $"data/guilds/{guildId}/{safeKind}.json"
                });
            }
        });

        app.MapGet("/api/vtc/shared/kinds", () =>
        {
            return Results.Ok(new
            {
                ok = true,
                kinds = new[]
                {
                    "branding",
                    "events",
                    "convoy",
                    "fleet",
                    "maintenance",
                    "roster-profiles",
                    "awards",
                    "settings"
                }
            });
        });
    }

    private static string NormalizeKind(string kind)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();

        return kind switch
        {
            "branding" => "branding",
            "events" => "events",
            "convoy" => "convoy",
            "fleet" => "fleet",
            "maintenance" => "maintenance",
            "roster-profiles" => "roster-profiles",
            "awards" => "awards",
            "settings" => "settings",
            _ => ""
        };
    }

    private static string GetGuildFilePath(string guildsDir, string guildId, string kind)
    {
        guildId = new string((guildId ?? "").Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(guildId))
            throw new InvalidOperationException("GuildId is required.");

        var dir = Path.Combine(guildsDir, guildId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{kind}.json");
    }

    private static string ReadJsonOrDefault(string path, string fallbackJson)
    {
        try
        {
            if (!File.Exists(path))
                return fallbackJson;

            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
                return fallbackJson;

            using var _ = JsonDocument.Parse(raw);
            return raw;
        }
        catch
        {
            return fallbackJson;
        }
    }

    private static string FormatJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string GetDefaultJsonForKind(string kind)
    {
        return kind switch
        {
            "branding" => """
            {
              "ok": true,
              "kind": "branding",
              "bannerImagePath": "",
              "iconImagePath": "",
              "updatedUtc": null
            }
            """,
            "events" => """
            {
              "ok": true,
              "kind": "events",
              "items": []
            }
            """,
            "convoy" => """
            {
              "ok": true,
              "kind": "convoy",
              "items": []
            }
            """,
            "fleet" => """
            {
              "ok": true,
              "kind": "fleet",
              "items": []
            }
            """,
            "maintenance" => """
            {
              "ok": true,
              "kind": "maintenance",
              "items": []
            }
            """,
            "roster-profiles" => """
            {
              "ok": true,
              "kind": "roster-profiles",
              "items": []
            }
            """,
            "awards" => """
            {
              "ok": true,
              "kind": "awards",
              "items": []
            }
            """,
            "settings" => """
            {
              "ok": true,
              "kind": "settings",
              "items": {}
            }
            """,
            _ => """
            {
              "ok": true,
              "items": []
            }
            """
        };
    }
}
