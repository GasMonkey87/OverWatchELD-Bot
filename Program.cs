using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using OverWatchELD.VtcBot.Commands;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Routes;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;

namespace OverWatchELD.VtcBot;

public static partial class Program
{
    private static DiscordSocketClient? _client;

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task Main(string[] args)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        var dispatchLoadStore = new DispatchLoadStore(
            Path.Combine(dataDir, "dispatch_loads.json"),
            JsonReadOpts,
            JsonWriteOpts);

        var dispatchMessageStore = new DispatchMessageStore(
            Path.Combine(dataDir, "dispatch_messages.json"),
            JsonReadOpts,
            JsonWriteOpts);

        var driverDisciplineStore = new DriverDisciplineStore(
            Path.Combine(dataDir, "driver_discipline.json"),
            JsonReadOpts,
            JsonWriteOpts);

        var services = new BotServices
        {
            ThreadStore = new ThreadMapStore(Path.Combine(dataDir, "thread_map.json")),
            DispatchStore = new DispatchSettingsStore(Path.Combine(dataDir, "dispatch_settings.json"), JsonReadOpts, JsonWriteOpts),
            RosterStore = new VtcRosterStore(Path.Combine(dataDir, "vtc_roster.json"), JsonReadOpts, JsonWriteOpts),
            LinkCodeStore = new LinkCodeStore(Path.Combine(dataDir, "link_codes.json"), JsonReadOpts, JsonWriteOpts),
            LinkedDriversStore = new LinkedDriversStore(Path.Combine(dataDir, "linked_drivers.json"), JsonReadOpts, JsonWriteOpts),
            PerformanceStore = new PerformanceStore(Path.Combine(dataDir, "performance"), JsonReadOpts, JsonWriteOpts),
            AwardStore = new VtcAwardStore(Path.Combine(dataDir, "vtc_awards.json"), JsonReadOpts, JsonWriteOpts),
            DriverAwardStore = new DriverAwardStore(Path.Combine(dataDir, "driver_awards.json"), JsonReadOpts, JsonWriteOpts),
            DriverStatusStore = new DriverStatusStore(Path.Combine(dataDir, "driver_status.json"), JsonReadOpts, JsonWriteOpts)
        };

        var token = (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Missing DISCORD_TOKEN env var.");
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
        services.DiscordReady = false;

        _client.Log += msg =>
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };

        _client.Ready += async () =>
        {
            services.DiscordReady = true;

            try
            {
                foreach (var guild in _client.Guilds)
                {
                    try { await guild.DownloadUsersAsync(); } catch { }
                }
            }
            catch
            {
            }
        };

        _client.MessageReceived += async rawMsg =>
        {
            try
            {
                if (rawMsg is not SocketUserMessage msg)
                    return;

                if (msg.Author.IsBot)
                    return;

                var text = (msg.Content ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (msg.Channel is SocketThreadChannel thread &&
                        thread.ParentChannel is SocketTextChannel parentText)
                    {
                        var guildId = parentText.Guild.Id.ToString();
                        var dispatchSettings = services.DispatchStore?.Get(guildId);
                        var dispatchChannelId = dispatchSettings?.DispatchChannelId ?? "";

                        if (string.Equals(parentText.Id.ToString(), dispatchChannelId, StringComparison.OrdinalIgnoreCase))
                        {
                            dispatchMessageStore.Add(new DispatchMessage
                            {
                                GuildId = guildId,
                                DriverDiscordUserId = msg.Author.Id.ToString(),
                                DriverName = msg.Author.Username,
                                Direction = "from_driver",
                                Text = text,
                                IsRead = false,
                                CreatedUtc = DateTimeOffset.UtcNow
                            });
                        }
                    }
                    else if (msg.Channel is SocketTextChannel textChannel)
                    {
                        var guildId = textChannel.Guild.Id.ToString();
                        var dispatchSettings = services.DispatchStore?.Get(guildId);
                        var dispatchChannelId = dispatchSettings?.DispatchChannelId ?? "";

                        if (string.Equals(textChannel.Id.ToString(), dispatchChannelId, StringComparison.OrdinalIgnoreCase))
                        {
                            dispatchMessageStore.Add(new DispatchMessage
                            {
                                GuildId = guildId,
                                DriverDiscordUserId = msg.Author.Id.ToString(),
                                DriverName = msg.Author.Username,
                                Direction = "from_driver",
                                Text = text,
                                IsRead = false,
                                CreatedUtc = DateTimeOffset.UtcNow
                            });
                        }
                    }
                }

                await HandleBuiltInDispatchCommandsAsync(msg, services);
                await BotCommandHandler.HandleMessageAsync(msg, services);
            }
            catch (Exception ex)
            {
                Console.WriteLine("MessageReceived error: " + ex);
            }
        };

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.IdleTimeout = TimeSpan.FromHours(12);
        });

        builder.Services.Configure<DiscordOAuthOptions>(builder.Configuration.GetSection("DiscordOAuth"));
        builder.Services.AddSingleton<WebSessionStore>();
        builder.Services.AddSingleton(new VtcAccessService(_client));
        builder.Services.AddHttpClient<DiscordOAuthService>();
        builder.Services.AddSingleton<PortalDataStore>();
        builder.Services.AddHttpClient();

        var portStr = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        if (!int.TryParse(portStr, out var port))
            port = 8080;

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto
        });

        app.UseSession();

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });
        }

        app.MapTelemetryRoutes();

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD Bot",
            discordReady = services.DiscordReady,
            guildCount = _client?.Guilds.Count ?? 0
        }));

        app.MapPost("/api/report-issue", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<IssueReportRequest>();

            if (req == null ||
                string.IsNullOrWhiteSpace(req.Email) ||
                string.IsNullOrWhiteSpace(req.Subject) ||
                string.IsNullOrWhiteSpace(req.Message))
            {
                return Results.BadRequest(new { ok = false, error = "MissingFields" });
            }

            var smtpUser = Environment.GetEnvironmentVariable("SMTP_USER");
            var smtpPass = Environment.GetEnvironmentVariable("SMTP_PASS");

            if (string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
            {
                return Results.Problem("SMTP is not configured.");
            }

            using var smtp = new System.Net.Mail.SmtpClient("smtp.gmail.com", 587)
            {
                EnableSsl = true,
                Credentials = new System.Net.NetworkCredential(smtpUser, smtpPass)
            };

            var mail = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress(smtpUser, "OverWatch ELD Website"),
                Subject = $"OverWatch ELD Issue: {req.Subject}",
                Body =
$@"New issue report from OverWatch ELD website.

Sender Email:
{req.Email}

Subject:
{req.Subject}

Message:
{req.Message}",
                IsBodyHtml = false
            };

            mail.To.Add("GasMonkeyCreations@gmail.com");
            mail.ReplyToList.Add(req.Email);

            await smtp.SendMailAsync(mail);

            return Results.Ok(new { ok = true });
        });

        app.MapGet("/build", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD Bot",
            utc = DateTimeOffset.UtcNow,
            discordReady = services.DiscordReady
        }));

        app.MapMapAssetRoutes();

        app.MapGet("/api/status", () => Results.Ok(new
        {
            ok = true,
            status = services.DiscordReady ? "online" : "starting",
            service = "OverWatchELD Bot",
            guilds = _client?.Guilds.Count ?? 0,
            uptimeSeconds = Math.Max(0L, Environment.TickCount64 / 1000),
            version = "2.0.0",
            discordReady = services.DiscordReady,
            utc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/", () => Results.Redirect("/index.html"));

        app.MapGet("/login", (HttpContext http, DiscordOAuthService oauth) =>
        {
            var state = Guid.NewGuid().ToString("N");

            http.Response.Cookies.Append("ow_oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                Secure = false,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10),
                IsEssential = true
            });

            var url = oauth.BuildAuthorizeUrl(state);
            return Results.Redirect(url);
        });

        app.MapGet("/download/latest", () =>
        {
            var url = "https://github.com/GasMonkey87/OverWatchELD-Bot/releases/download/V2.0.7/OverWatchELD-win-Setup.exe";
            return Results.Redirect(url);
        });

        app.MapGet("/auth/discord/callback", async (
            HttpContext http,
            DiscordOAuthService oauth,
            WebSessionStore sessions,
            VtcAccessService vtcAccess,
            CancellationToken ct) =>
        {
            var error = http.Request.Query["error"].ToString();
            if (!string.IsNullOrWhiteSpace(error))
                return Results.Redirect("/?authError=discord_denied");

            var code = http.Request.Query["code"].ToString();
            var state = http.Request.Query["state"].ToString();
            var expectedState = http.Request.Cookies["ow_oauth_state"];

            if (string.IsNullOrWhiteSpace(code) ||
                string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return Results.Redirect("/?authError=invalid_state");
            }

            var tokenRes = await oauth.ExchangeCodeAsync(code, ct);
            if (tokenRes == null || string.IsNullOrWhiteSpace(tokenRes.AccessToken))
                return Results.Redirect("/?authError=token_exchange_failed");

            var user = await oauth.GetCurrentUserAsync(tokenRes.AccessToken, ct);
            if (user == null || string.IsNullOrWhiteSpace(user.Id))
                return Results.Redirect("/?authError=user_fetch_failed");

            var guilds = await oauth.GetCurrentUserGuildsAsync(tokenRes.AccessToken, ct);
            var matches = vtcAccess.MatchSupportedVtcs(user.Id, guilds);

            var sessionId = Guid.NewGuid().ToString("N");
            sessions.Save(sessionId, new WebSessionUser
            {
                DiscordUserId = user.Id,
                Username = user.Username,
                GlobalName = user.GlobalName,
                AccessToken = tokenRes.AccessToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

            http.Response.Cookies.Append("ow_session", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = false,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddHours(8),
                IsEssential = true
            });

            http.Session.SetString("discord_user", JsonSerializer.Serialize(new
            {
                id = user.Id,
                username = user.Username,
                global_name = user.GlobalName ?? "",
                avatar = user.Avatar ?? ""
            }));

            http.Session.SetString("discord_guilds", JsonSerializer.Serialize(
                guilds.Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    owner = g.Owner,
                    permissions = g.Permissions ?? "0",
                    permissions_new = g.Permissions ?? "0"
                })
            ));

            if (matches.Count == 0)
                return Results.Redirect("/?authError=no_supported_vtc");

            if (matches.Count == 1)
            {
                var only = matches[0];

                http.Response.Cookies.Append("ow_selected_guild", only.GuildId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddHours(8),
                    IsEssential = true
                });

                var redirect = only.IsManager
                    ? $"/manage.html?guildId={Uri.EscapeDataString(only.GuildId)}"
                    : $"/portal.html?guildId={Uri.EscapeDataString(only.GuildId)}";

                return Results.Redirect(redirect);
            }

            return Results.Redirect("/select-vtc.html");
        });

        app.MapGet("/api/auth/me", (
            HttpContext http,
            WebSessionStore sessions) =>
        {
            var sessionId = http.Request.Cookies["ow_session"];
            if (string.IsNullOrWhiteSpace(sessionId) ||
                !sessions.TryGet(sessionId, out var user) ||
                user == null)
            {
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);
            }

            return Results.Ok(new
            {
                ok = true,
                data = new
                {
                    discordUserId = user.DiscordUserId,
                    username = user.Username,
                    globalName = user.GlobalName
                }
            });
        });

        app.MapGet("/api/auth/vtcs", async (
            HttpContext http,
            WebSessionStore sessions,
            DiscordOAuthService oauth,
            VtcAccessService vtcAccess,
            CancellationToken ct) =>
        {
            var sessionId = http.Request.Cookies["ow_session"];
            if (string.IsNullOrWhiteSpace(sessionId) ||
                !sessions.TryGet(sessionId, out var session) ||
                session == null)
            {
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);
            }

            var guilds = await oauth.GetCurrentUserGuildsAsync(session.AccessToken, ct);
            var matches = vtcAccess.MatchSupportedVtcs(session.DiscordUserId, guilds);

            return Results.Ok(new
            {
                ok = true,
                data = matches.Select(x => new
                {
                    guildId = x.GuildId,
                    vtcName = x.VtcName,
                    logoUrl = x.LogoUrl,
                    role = x.Role,
                    isManager = x.IsManager
                })
            });
        });

        app.MapPost("/api/auth/select-vtc", async (
            HttpContext http,
            DiscordSelectVtcRequest request,
            WebSessionStore sessions,
            DiscordOAuthService oauth,
            VtcAccessService vtcAccess,
            CancellationToken ct) =>
        {
            var sessionId = http.Request.Cookies["ow_session"];
            if (string.IsNullOrWhiteSpace(sessionId) ||
                !sessions.TryGet(sessionId, out var session) ||
                session == null)
            {
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);
            }

            if (request == null || string.IsNullOrWhiteSpace(request.GuildId))
                return Results.Json(new { ok = false, error = "GuildIdRequired" }, statusCode: 400);

            var guilds = await oauth.GetCurrentUserGuildsAsync(session.AccessToken, ct);
            var matches = vtcAccess.MatchSupportedVtcs(session.DiscordUserId, guilds);
            var selected = matches.FirstOrDefault(x => x.GuildId == request.GuildId);

            if (selected == null)
                return Results.Json(new { ok = false, error = "GuildNotAllowed" }, statusCode: 403);

            http.Response.Cookies.Append("ow_selected_guild", selected.GuildId, new CookieOptions
            {
                HttpOnly = true,
                Secure = false,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddHours(8),
                IsEssential = true
            });

            var redirect = selected.IsManager
                ? $"/manage.html?guildId={Uri.EscapeDataString(selected.GuildId)}"
                : $"/portal.html?guildId={Uri.EscapeDataString(selected.GuildId)}";

            return Results.Ok(new
            {
                ok = true,
                redirectUrl = redirect
            });
        });

        app.MapPost("/logout", (HttpContext http, WebSessionStore sessions) =>
        {
            var sessionId = http.Request.Cookies["ow_session"];
            if (!string.IsNullOrWhiteSpace(sessionId))
                sessions.Remove(sessionId);

            http.Response.Cookies.Delete("ow_session");
            http.Response.Cookies.Delete("ow_selected_guild");
            http.Response.Cookies.Delete("ow_oauth_state");

            http.Session.Remove("discord_user");
            http.Session.Remove("discord_guilds");

            return Results.Ok(new { ok = true });
        });

        AutoSetupRoutes.Register(app, services, JsonWriteOpts);
        DashboardRoutes.Register(app, services, JsonWriteOpts);

        var loadThreadStore = new ProgramLoadThreadStore(Path.Combine(dataDir, "load_threads.json"), JsonWriteOpts);
        var loadApiLogPath = Path.Combine(dataDir, "load_api_log.txt");

        app.MapMethods("/api/loads/pickup", new[] { "POST", "GET" }, async (HttpRequest req) =>
        {
            var dto = await ReadLoadDtoAsync(req, loadApiLogPath, "pickup");
            if (dto == null || string.IsNullOrWhiteSpace(dto.LoadNumber))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BadJson",
                    hint = "Send JSON or query params with loadNumber/currentLoadNumber plus optional driver, truck, cargo, weight, startLocation, endLocation"
                }, statusCode: 400);
            }

            var result = await PostLoadPickup(_client, services.DispatchStore, loadThreadStore, dto, loadApiLogPath);
            return Results.Ok(new
            {
                ok = true,
                threadCreated = result.ThreadCreated,
                threadId = result.ThreadId,
                reason = result.Reason,
                fallbackPosted = result.FallbackPosted
            });
        });

        app.MapMethods("/api/loads/complete", new[] { "POST", "GET" }, async (HttpRequest req) =>
        {
            var dto = await ReadLoadDtoAsync(req, loadApiLogPath, "complete");
            if (dto == null || string.IsNullOrWhiteSpace(dto.LoadNumber))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BadJson",
                    hint = "Send JSON or query params with loadNumber/currentLoadNumber plus optional driver, truck, cargo, weight, startLocation, endLocation"
                }, statusCode: 400);
            }

            var result = await PostLoadComplete(_client, loadThreadStore, dto, loadApiLogPath);
            return Results.Ok(new
            {
                ok = true,
                archived = result.Archived,
                reason = result.Reason,
                fallbackPosted = result.FallbackPosted
            });
        });

        using var sharedHttp = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        ApiRoutes.Register(app, services, JsonReadOpts, JsonWriteOpts, sharedHttp);
        AwardRoutes.Register(app, services, JsonWriteOpts);
        DispatchRoutes.Register(app, services, JsonWriteOpts, dispatchLoadStore, dispatchMessageStore);
        ManagementRoutes.Register(app, services, dispatchMessageStore, driverDisciplineStore);

        RegisterProgramRoutes(app, services, dataDir);
        app.MapPortalDataRoutes();

        Console.WriteLine($"Bot running on :{port}");
        await app.RunAsync();
    }
}

public sealed class IssueReportRequest
{
    public string? Email { get; set; }
    public string? Subject { get; set; }
    public string? Message { get; set; }
}
