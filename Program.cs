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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using OverWatchELD.VtcBot.Commands;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Routes;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;
using OverWatchELD.VtcBot.Hubs;

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

        var portalBaseUrl = (Environment.GetEnvironmentVariable("OVERWATCH_PORTAL_BASE_URL")
            ?? Environment.GetEnvironmentVariable("PUBLIC_PORTAL_BASE_URL")
            ?? "https://overwatcheld.com")
            .Trim()
            .TrimEnd('/');
        
        var emailAccountStore = new EmailAccountStore(
    Path.Combine(dataDir, "overwatcheld_accounts.db"));
        
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
        builder.Services.AddSingleton(_client);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.None;
            options.IdleTimeout = TimeSpan.FromHours(12);
        });

        builder.Services.Configure<DiscordOAuthOptions>(builder.Configuration.GetSection("DiscordOAuth"));
        builder.Services.AddSingleton(new WebSessionStore(Path.Combine(dataDir, "overwatcheld_sessions.db")));
        builder.Services.AddSingleton(new VtcAccessService(_client));
        builder.Services.AddHttpClient<DiscordOAuthService>();
        builder.Services.AddSingleton<PortalDataStore>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<GuildSettingsStore>();
        builder.Services.AddSignalR();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("CloudflarePortal", policy =>
            {
                policy
                    .SetIsOriginAllowed(origin =>
                    {
                        if (string.IsNullOrWhiteSpace(origin))
                            return false;

                        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                            return false;

                        var host = uri.Host.ToLowerInvariant();

                        return host == "overwatcheld.com" ||
                               host == "www.overwatcheld.com" ||
                               host == "overwatcheld.pages.dev" ||
                               host.EndsWith(".overwatcheld.pages.dev", StringComparison.OrdinalIgnoreCase);
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
        builder.Services.AddSingleton<PersistentDispatchMessageStore>();

        var portStr = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        if (!int.TryParse(portStr, out var port))
            port = 8080;

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.UseCors("CloudflarePortal");

        await app.Services.GetRequiredService<PersistentDispatchMessageStore>().EnsureCreatedAsync();
        services.GuildSettingsStore = app.Services.GetRequiredService<GuildSettingsStore>();

        try
{
    using var scope = app.Services.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
    await store.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DB] Guild settings init failed: " + ex.Message);
        }

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
        app.MapHub<DispatchHub>("/hubs/dispatch");
        app.MapGet("/api/vtc/member/role", async (HttpContext ctx) =>
{
    var guildId = ctx.Request.Query["guildId"].ToString();
    var discordUserId = ctx.Request.Query["discordUserId"].ToString();

    if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(discordUserId))
    {
        return Results.Json(new { ok = false, error = "MissingParams" });
    }

    try
    {
        var discord = ctx.RequestServices.GetRequiredService<DiscordSocketClient>();

        var guild = discord.GetGuild(ulong.Parse(guildId));
        if (guild == null)
        {
            return Results.Json(new { ok = false, error = "GuildNotFound" });
        }

        var user = guild.GetUser(ulong.Parse(discordUserId));
        if (user == null)
        {
            return Results.Json(new { ok = false, error = "UserNotFound" });
        }

        // 🔥 ROLE DETECTION LOGIC
        string role = "Driver";

        if (guild.OwnerId == user.Id)
        {
            role = "Owner";
        }
        else
        {
            var roleNames = user.Roles.Select(r => r.Name.ToLower()).ToList();

            if (roleNames.Any(r => r.Contains("admin")))
                role = "Admin";
            else if (roleNames.Any(r => r.Contains("manager")))
                role = "Manager";
        }

        return Results.Json(new
        {
            ok = true,
            role = role,
            linkedUserRole = role
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            ok = false,
            error = "Exception",
            message = ex.Message
        });
    }
});
        
        app.MapGet("/api/updates/latest", () =>
{
    try
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "version.json");

        if (!File.Exists(path))
        {
            return Results.Json(new
            {
                ok = false,
                error = "version file missing"
            });
        }

        var json = File.ReadAllText(path);
        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            ok = false,
            error = ex.Message
        });
    }
});
        
        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD Bot",
            discordReady = services.DiscordReady,
            guildCount = _client?.Guilds.Count ?? 0
        }));

        app.MapVtcDiscordAutoSetupRoutes(_client);
        
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

        app.MapGet("/api/fleet/truck-approved", () => Results.Json(new
{
    ok = true,
    route = "/api/fleet/truck-approved",
    methods = "POST",
    message = "Fleet truck approval route is deployed."
}));
        
        app.MapMapAssetRoutes(); // fine if method exists

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
        app.MapPost("/api/fleet/truck-approved", async (
            HttpContext ctx,
            DiscordSocketClient discord,
            GuildSettingsStore settingsStore) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<TruckApprovedDiscordRequest>();

            if (req == null || string.IsNullOrWhiteSpace(req.GuildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            if (!ulong.TryParse(req.GuildId, out var guildId))
                return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

            var guild = discord.GetGuild(guildId);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" });

            var settings = await settingsStore.GetAsync(req.GuildId);

            SocketTextChannel? channel = null;

            if (ulong.TryParse(req.FleetChannelId, out var explicitChannelId))
                channel = guild.GetTextChannel(explicitChannelId);

            // Prefer an actual fleet/truck channel by name so this posts in the fleet channel,
            // even though older vtc_config.json files do not have a FleetChannelId field yet.
            channel ??= FindFleetTextChannel(guild);

            // Safe fallback if the server does not have a fleet-named channel yet.
            channel ??= ResolveTextChannelById(guild, settings.LoadboardChannelId);
            channel ??= ResolveTextChannelById(guild, settings.AnnouncementsChannelId);
            channel ??= ResolveTextChannelById(guild, settings.DispatchChannelId);

            if (channel == null)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "FleetChannelNotConfigured",
                    hint = "Create a Discord text channel named fleet, fleet-trucks, vtc-fleet, or trucks, or configure a fallback channel."
                });
            }

            var embed = new EmbedBuilder()
                .WithTitle("🚛 New Fleet Truck Approved")
                .WithColor(Color.Green)
                .AddField("Truck #", FirstNonBlank(req.TruckNumber, "N/A"), true)
                .AddField("Driver", FirstNonBlank(req.DriverName, "Unassigned"), true)
                .AddField("Truck", FirstNonBlank(req.TruckName, req.Model, "Unknown"), true)
                .AddField("Plate", FirstNonBlank(req.Plate, "N/A"), true)
                .AddField("Mileage", FirstNonBlank(req.Mileage, "N/A"), true)
                .AddField("Status", "Approved", true)
                .WithFooter("OverWatch ELD Fleet Management")
                .WithCurrentTimestamp()
                .Build();

            await channel.SendMessageAsync(embed: embed);

            return Results.Json(new
            {
                ok = true,
                channelId = channel.Id.ToString(),
                channelName = channel.Name
            });
        });

        app.MapGet("/api/onboarding/me", (HttpContext http, WebSessionStore sessions) =>
{
    var sessionId = http.Request.Cookies["ow_session"];

    if (string.IsNullOrWhiteSpace(sessionId) ||
        !sessions.TryGet(sessionId, out var user) ||
        user == null)
    {
        return Results.Ok(new { loggedIn = false });
    }

    return Results.Ok(new
    {
        loggedIn = true,
        username = user.Username,
        discordUserId = user.DiscordUserId
    });
});
        app.MapGet("/", () => Results.Ok(new { ok = true, service = "OverWatchELD API", portal = portalBaseUrl }));

        app.MapGet("/auth/login", (HttpContext http) =>
        {
            var returnTo = http.Request.Query["returnTo"].ToString();
            var target = string.IsNullOrWhiteSpace(returnTo)
                ? "/login"
                : $"/login?returnTo={Uri.EscapeDataString(returnTo)}";
            return Results.Redirect(target);
        });

        app.MapGet("/login", (HttpContext http, DiscordOAuthService oauth, WebSessionStore sessions) =>
        {
            var state = Guid.NewGuid().ToString("N");

            http.Response.Cookies.Append("ow_oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10),
                IsEssential = true
            });

            var linkDiscord = http.Request.Query["linkDiscord"].ToString();
            if (linkDiscord == "1" || linkDiscord.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.Cookies.Append("ow_link_discord", "1", new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    Expires = DateTimeOffset.UtcNow.AddMinutes(10),
                    IsEssential = true
                });

                // Save the currently logged-in website account id during the OAuth trip.
                // This makes Discord linking reliable even if the browser/session cookie is not
                // available after returning from Discord.
                var currentSessionIdForLink = http.Request.Cookies["ow_session"];
                if (!string.IsNullOrWhiteSpace(currentSessionIdForLink) &&
                    sessions.TryGet(currentSessionIdForLink, out var currentUserForLink) &&
                    currentUserForLink != null &&
                    !string.IsNullOrWhiteSpace(currentUserForLink.AccountId))
                {
                    http.Response.Cookies.Append("ow_link_account_id", currentUserForLink.AccountId, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.None,
                        Expires = DateTimeOffset.UtcNow.AddMinutes(10),
                        IsEssential = true
                    });
                }
            }
            else
            {
                http.Response.Cookies.Delete("ow_link_discord");
                http.Response.Cookies.Delete("ow_link_account_id");
            }

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
                return Results.Redirect($"{portalBaseUrl}/driver-home.html?linkDiscord=denied");

            var code = http.Request.Query["code"].ToString();
            var state = http.Request.Query["state"].ToString();
            var expectedState = http.Request.Cookies["ow_oauth_state"];

            if (string.IsNullOrWhiteSpace(code) ||
                string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return Results.Redirect($"{portalBaseUrl}/driver-home.html?linkDiscord=invalid_state");
            }

            var tokenRes = await oauth.ExchangeCodeAsync(code, ct);
            if (tokenRes == null || string.IsNullOrWhiteSpace(tokenRes.AccessToken))
                return Results.Redirect($"{portalBaseUrl}/driver-home.html?linkDiscord=token_failed");

            var user = await oauth.GetCurrentUserAsync(tokenRes.AccessToken, ct);
            if (user == null || string.IsNullOrWhiteSpace(user.Id))
                return Results.Redirect($"{portalBaseUrl}/driver-home.html?linkDiscord=user_failed");

            var isLinkDiscord = string.Equals(http.Request.Cookies["ow_link_discord"], "1", StringComparison.Ordinal);
            var currentSessionId = http.Request.Cookies["ow_session"];

            WebSessionUser? currentSession = null;
            if (!string.IsNullOrWhiteSpace(currentSessionId))
                sessions.TryGet(currentSessionId, out currentSession);

            var linkAccountId = currentSession?.AccountId;
            if (string.IsNullOrWhiteSpace(linkAccountId))
                linkAccountId = http.Request.Cookies["ow_link_account_id"];

            if (isLinkDiscord && !string.IsNullOrWhiteSpace(linkAccountId))
            {
                var linked = emailAccountStore.LinkDiscord(
                    linkAccountId,
                    user.Id,
                    user.Username ?? "",
                    user.GlobalName ?? "");

                if (linked == null)
                {
                    http.Response.Cookies.Delete("ow_link_discord");
                    http.Response.Cookies.Delete("ow_link_account_id");
                    http.Response.Cookies.Delete("ow_oauth_state");
                    return Results.Redirect($"{portalBaseUrl}/profile.html?linkDiscord=account_not_found");
                }

                var sessionToSave = !string.IsNullOrWhiteSpace(currentSessionId)
                    ? currentSessionId!
                    : Guid.NewGuid().ToString("N");

                sessions.Save(sessionToSave, new WebSessionUser
                {
                    AccountId = linked.Id,
                    Email = linked.Email,
                    IsEmailAccount = true,
                    DiscordUserId = user.Id,
                    Username = string.IsNullOrWhiteSpace(linked.DisplayName) ? user.Username : linked.DisplayName,
                    GlobalName = string.IsNullOrWhiteSpace(user.GlobalName) ? linked.DisplayName : user.GlobalName,
                    AccessToken = tokenRes.AccessToken,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                });

                http.Response.Cookies.Append("ow_session", sessionToSave, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                    IsEssential = true
                });

                http.Response.Cookies.Delete("ow_link_discord");
                http.Response.Cookies.Delete("ow_link_account_id");
                http.Response.Cookies.Delete("ow_oauth_state");

                return Results.Redirect($"{portalBaseUrl}/driver-home.html?linkDiscord=success");
            }

            var guilds = await oauth.GetCurrentUserGuildsAsync(tokenRes.AccessToken, ct);
            var matches = vtcAccess.MatchSupportedVtcs(user.Id, guilds);

            var existingLinkedAccount = emailAccountStore.FindByDiscordUserId(user.Id);

            var sessionId = Guid.NewGuid().ToString("N");
            sessions.Save(sessionId, new WebSessionUser
            {
                AccountId = existingLinkedAccount?.Id ?? "",
                Email = existingLinkedAccount?.Email ?? "",
                IsEmailAccount = existingLinkedAccount != null,
                DiscordUserId = user.Id,
                Username = existingLinkedAccount?.DisplayName ?? user.Username,
                GlobalName = user.GlobalName ?? existingLinkedAccount?.DisplayName,
                AccessToken = tokenRes.AccessToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

            http.Response.Cookies.Append("ow_session", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddHours(8),
                IsEssential = true
            });

            http.Response.Cookies.Delete("ow_link_discord");
            http.Response.Cookies.Delete("ow_link_account_id");
            http.Response.Cookies.Delete("ow_oauth_state");

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
                return Results.Redirect($"{portalBaseUrl}/driver-home.html?discordLinked=1");

            if (matches.Count == 1)
            {
                var only = matches[0];

                http.Response.Cookies.Append("ow_selected_guild", only.GuildId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    Expires = DateTimeOffset.UtcNow.AddHours(8),
                    IsEssential = true
                });

                var redirect = only.IsManager
    ? $"{portalBaseUrl}/vtc-dashboard.html?guildId={Uri.EscapeDataString(only.GuildId)}"
    : $"{portalBaseUrl}/driver-home.html?guildId={Uri.EscapeDataString(only.GuildId)}";

                return Results.Redirect(redirect);
            }

            return Results.Redirect($"{portalBaseUrl}/vtc-dashboard.html");
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
                    accountId = user.AccountId,
                    email = user.Email,
                    isEmailAccount = user.IsEmailAccount,
                    discordLinked = !string.IsNullOrWhiteSpace(user.DiscordUserId),
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
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddHours(8),
                IsEssential = true
            });

            var redirect = selected.IsManager
    ? $"{portalBaseUrl}/vtc-dashboard.html?guildId={Uri.EscapeDataString(selected.GuildId)}"
    : $"{portalBaseUrl}/driver-home.html?guildId={Uri.EscapeDataString(selected.GuildId)}";

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
        app.MapGet("/api/onboarding/status", () =>
{
    var botClientId = Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID") ?? "YOUR_CLIENT_ID";

    return Results.Ok(new
    {
        ok = true,
        discordReady = services.DiscordReady,
        guildCount = _client?.Guilds.Count ?? 0,
        latestVersion = "2.0.7",
        downloadUrl = "/download/latest",
        setupUrl = "/setup.html",
        botInviteUrl = $"https://discord.com/oauth2/authorize?client_id=1469496462294520081&scope=bot%20applications.commands&permissions=8"
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
        VtcManagementRoutes.Register(app);
        app.MapPortalDataRoutes();
        BolUploadRoutes.Register(app.MapGroup("/api"), services, JsonWriteOpts);
        Console.WriteLine($"Bot running on :{port}");
        EmailAccountRoutes.Register(
    app,
    emailAccountStore,
    app.Services.GetRequiredService<WebSessionStore>());
        await app.RunAsync();
    }

    private static SocketTextChannel? ResolveTextChannelById(SocketGuild guild, string? channelIdText)
    {
        if (guild == null || string.IsNullOrWhiteSpace(channelIdText))
            return null;

        return ulong.TryParse(channelIdText.Trim(), out var channelId)
            ? guild.GetTextChannel(channelId)
            : null;
    }

    private static SocketTextChannel? FindFleetTextChannel(SocketGuild guild)
    {
        if (guild == null)
            return null;

        var preferredNames = new[]
        {
            "fleet-trucks",
            "fleet_trucks",
            "fleet trucks",
            "vtc-fleet",
            "vtc_fleet",
            "fleet",
            "trucks",
            "truck-approvals",
            "truck_approvals"
        };

        foreach (var wanted in preferredNames)
        {
            var match = guild.TextChannels.FirstOrDefault(c =>
                string.Equals(NormalizeChannelName(c.Name), NormalizeChannelName(wanted), StringComparison.OrdinalIgnoreCase));

            if (match != null)
                return match;
        }

        return guild.TextChannels.FirstOrDefault(c =>
        {
            var name = NormalizeChannelName(c.Name);
            return name.Contains("fleet") || name.Contains("truck");
        });
    }

    private static string NormalizeChannelName(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }

public sealed class TruckApprovedDiscordRequest
{
    public string GuildId { get; set; } = "";
    public string FleetChannelId { get; set; } = "";
    public string TruckNumber { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string TruckName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Plate { get; set; } = "";
    public string Mileage { get; set; } = "";
}

public sealed class IssueReportRequest
{
    public string? Email { get; set; }
    public string? Subject { get; set; }
    public string? Message { get; set; }
}
}
