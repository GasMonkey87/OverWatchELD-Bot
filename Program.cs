using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using OverWatchELD.VtcBot.Commands;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Routes;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;

namespace OverWatchELD.VtcBot;

public static class Program
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

        _client.MessageReceived += async msg =>
        {
            await HandleBuiltInDispatchCommandsAsync(msg, services);
            await BotCommandHandler.HandleMessageAsync(msg, services);
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

        var portStr = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        if (!int.TryParse(portStr, out var port))
            port = 8080;

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

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

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD Bot",
            discordReady = services.DiscordReady,
            guildCount = _client?.Guilds.Count ?? 0
        }));

        app.MapGet("/build", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD Bot",
            utc = DateTimeOffset.UtcNow,
            discordReady = services.DiscordReady
        }));

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

        DashboardRoutes.Register(app, services, JsonWriteOpts);
        AuthRoutes.Register(app);

        var loadThreadStore = new ProgramLoadThreadStore(Path.Combine(dataDir, "load_threads.json"), JsonWriteOpts);
        var loadApiLogPath = Path.Combine(dataDir, "load_api_log.txt");

        var dispatchLoadStore = new DispatchLoadStore(
            Path.Combine(dataDir, "dispatch_loads.json"),
            JsonReadOpts,
            JsonWriteOpts);

        var dispatchMessageStore = new DispatchMessageStore(
            Path.Combine(dataDir, "dispatch_messages.json"),
            JsonReadOpts,
            JsonWriteOpts);

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

        app.MapPost("/api/vtc/loadboard/settings", async (HttpRequest req) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                var root = doc.RootElement;

                var guildId = FirstString(root, "guildId", "GuildId");
                if (string.IsNullOrWhiteSpace(guildId))
                    guildId = _client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

                var dispatchChannelId = FirstString(root,
                    "dispatchChannelId", "DispatchChannelId",
                    "loadboardChannelId", "LoadboardChannelId",
                    "channelId", "ChannelId");

                if (string.IsNullOrWhiteSpace(guildId) ||
                    string.IsNullOrWhiteSpace(dispatchChannelId) ||
                    !ulong.TryParse(dispatchChannelId, out var chId) ||
                    chId == 0)
                {
                    return Results.Json(new { ok = false, error = "MissingGuildIdOrChannelId" }, statusCode: 400);
                }

                services.DispatchStore?.SetDispatchChannel(guildId, chId);
                return Results.Ok(new { ok = true, guildId, dispatchChannelId = chId.ToString() });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        app.MapMethods("/api/eld/driver/status", new[] { "POST" }, async (HttpRequest req) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                var root = doc.RootElement;

                string ReadString(params string[] names)
                {
                    foreach (var name in names)
                    {
                        if (root.TryGetProperty(name, out var p))
                        {
                            var s = p.ToString()?.Trim() ?? "";
                            if (!string.IsNullOrWhiteSpace(s))
                                return s;
                        }
                    }
                    return "";
                }

                double ReadDouble(params string[] names)
                {
                    var text = ReadString(names);
                    if (!string.IsNullOrWhiteSpace(text) && double.TryParse(text, out var value))
                        return value;
                    return 0;
                }

                var guildId = ReadString("guildId", "GuildId");
                if (string.IsNullOrWhiteSpace(guildId))
                    guildId = _client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

                var discordUserId = ReadString("discordUserId", "DiscordUserId", "userId", "UserId");
                var driverName = ReadString("driverName", "DriverName", "discordUsername", "DiscordUsername", "name", "Name");
                var dutyStatus = ReadString("dutyStatus", "DutyStatus", "duty", "Duty");
                var truck = ReadString("truck", "Truck", "truckId", "TruckId");
                var loadNumber = ReadString("loadNumber", "LoadNumber", "currentLoadNumber", "CurrentLoadNumber");
                var location = ReadString("location", "Location", "locationText", "LocationText");

                var speedMph = ReadDouble("speedMph", "SpeedMph", "speed", "Speed");
                var latitude = ReadDouble("latitude", "Latitude", "lat", "Lat");
                var longitude = ReadDouble("longitude", "Longitude", "lon", "Lon", "lng", "Lng");
                var heading = ReadDouble("heading", "Heading");

                if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(discordUserId))
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "MissingGuildIdOrDiscordUserId"
                    }, statusCode: 400);
                }

                services.DriverStatusStore?.Upsert(new DriverStatusStore.DriverStatusEntry
                {
                    GuildId = guildId,
                    DiscordUserId = discordUserId,
                    DriverName = driverName,
                    DutyStatus = dutyStatus,
                    Truck = truck,
                    LoadNumber = loadNumber,
                    Location = location,
                    SpeedMph = speedMph,
                    Latitude = latitude,
                    Longitude = longitude,
                    Heading = heading,
                    LastSeenUtc = DateTimeOffset.UtcNow
                });

                return Results.Ok(new
                {
                    ok = true,
                    guildId,
                    discordUserId,
                    updatedUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = ex.Message
                }, statusCode: 500);
            }
        });

        app.MapGet("/api/eld/driver/status", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                guildId = _client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Ok(new { ok = true, rows = Array.Empty<object>() });

            var rows = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                rows
            });
        });

        app.MapGet("/api/map/live", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                guildId = _client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(guildId))
            {
                return Results.Ok(new
                {
                    ok = true,
                    guildId = "",
                    points = Array.Empty<object>()
                });
            }

            var rows = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();

            var points = rows
                .Where(x => Math.Abs(x.Latitude) > 0.000001 || Math.Abs(x.Longitude) > 0.000001)
                .Select(x => new
                {
                    discordUserId = x.DiscordUserId,
                    driverName = x.DriverName,
                    dutyStatus = x.DutyStatus,
                    truck = x.Truck,
                    loadNumber = x.LoadNumber,
                    location = x.Location,
                    speedMph = x.SpeedMph,
                    latitude = x.Latitude,
                    longitude = x.Longitude,
                    heading = x.Heading,
                    lastSeenUtc = x.LastSeenUtc
                })
                .ToList();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                points
            });
        });

        // =============================
        // ELD <-> DISPATCH MESSAGING BRIDGE
        // =============================

        app.MapGet("/api/messages/thread", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var discordUserId = (req.Query["discordUserId"].ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(discordUserId))
            {
                return Results.Json(new { ok = false, error = "MissingGuildIdOrUserId" }, statusCode: 400);
            }

            var rows = dispatchMessageStore.ListConversation(guildId, discordUserId);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                rows
            });
        });

        app.MapGet("/api/messages/conversations", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            var discordUserId = (req.Query["discordUserId"].ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(discordUserId))
            {
                return Results.Json(new { ok = false, error = "MissingGuildIdOrUserId" }, statusCode: 400);
            }

            var rows = dispatchMessageStore.List(guildId)
                .Where(x => string.Equals(x.DriverDiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedUtc)
                .Take(50)
                .ToList();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                rows
            });
        });

        app.MapPost("/api/messages/send", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            string Read(params string[] names)
            {
                foreach (var n in names)
                {
                    if (root.TryGetProperty(n, out var v))
                    {
                        var s = v.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s;
                    }
                }
                return "";
            }

            var guildId = Read("guildId", "GuildId");
            var discordUserId = Read("discordUserId", "DiscordUserId");
            var driverName = Read("driverName", "DriverName", "discordUsername", "DiscordUsername");
            var text = Read("text", "Text", "message", "Message");
            var loadNumber = Read("loadNumber", "LoadNumber");

            if (string.IsNullOrWhiteSpace(guildId) ||
                string.IsNullOrWhiteSpace(discordUserId) ||
                string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new { ok = false, error = "MissingFields" }, statusCode: 400);
            }

            var saved = dispatchMessageStore.Add(new DispatchMessage
            {
                GuildId = guildId,
                DriverDiscordUserId = discordUserId,
                DriverName = driverName,
                Direction = "from_driver",
                Text = text,
                LoadNumber = loadNumber,
                IsRead = false
            });

            return Results.Ok(new
            {
                ok = true,
                message = saved
            });
        });

        Console.WriteLine($"Bot running on :{port}");
        await app.RunAsync();
    }

    private static async Task HandleBuiltInDispatchCommandsAsync(SocketMessage rawMsg, BotServices services)
    {
        try
        {
            if (rawMsg is not SocketUserMessage msg)
                return;

            if (msg.Author.IsBot)
                return;

            var text = (msg.Content ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!text.Equals("!setdispatchchannel", StringComparison.OrdinalIgnoreCase) &&
                !text.Equals("!setupdispatch", StringComparison.OrdinalIgnoreCase))
                return;

            if (msg.Channel is not SocketGuildChannel guildChannel)
            {
                await msg.Channel.SendMessageAsync("Run that command inside your Discord server.");
                return;
            }

            var guildId = guildChannel.Guild.Id.ToString();
            ulong channelIdToSave = guildChannel.Id;

            if (guildChannel is SocketThreadChannel thread && thread.ParentChannel is SocketTextChannel parentText)
                channelIdToSave = parentText.Id;

            services.DispatchStore?.SetDispatchChannel(guildId, channelIdToSave);

            await msg.Channel.SendMessageAsync($"Dispatch channel saved: <#{channelIdToSave}>");
        }
        catch
        {
        }
    }

    private static async Task<LoadDto?> ReadLoadDtoAsync(HttpRequest req, string logPath, string routeName)
    {
        try
        {
            string rawBody = "";
            JsonElement body = default;
            var hasBody = false;

            if (HttpMethods.IsPost(req.Method))
            {
                req.EnableBuffering();
                using (var reader = new StreamReader(req.Body, leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync();
                }
                req.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(rawBody))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(rawBody);
                        body = doc.RootElement.Clone();
                        hasBody = true;
                    }
                    catch
                    {
                    }
                }
            }

            string q(string name) => req.Query.TryGetValue(name, out var v) ? (v.ToString() ?? "").Trim() : "";

            string Pick(JsonElement e, params string[] names)
            {
                if (e.ValueKind != JsonValueKind.Object)
                    return "";

                foreach (var name in names)
                {
                    if (e.TryGetProperty(name, out var prop))
                    {
                        var value = (prop.ToString() ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }

                return "";
            }

            var loadNumber = hasBody ? Pick(body, "loadNumber", "currentLoadNumber", "loadNo") : "";
            if (string.IsNullOrWhiteSpace(loadNumber))
                loadNumber = FirstNonBlank(q("loadNumber"), q("currentLoadNumber"), q("loadNo"));

            var driver = hasBody ? Pick(body, "driver", "driverName") : "";
            if (string.IsNullOrWhiteSpace(driver))
                driver = FirstNonBlank(q("driver"), q("driverName"));

            var truck = hasBody ? Pick(body, "truck", "truckName") : "";
            if (string.IsNullOrWhiteSpace(truck))
                truck = FirstNonBlank(q("truck"), q("truckName"));

            var cargo = hasBody ? Pick(body, "cargo", "commodity") : "";
            if (string.IsNullOrWhiteSpace(cargo))
                cargo = FirstNonBlank(q("cargo"), q("commodity"));

            var startLocation = hasBody ? Pick(body, "startLocation", "origin") : "";
            if (string.IsNullOrWhiteSpace(startLocation))
                startLocation = FirstNonBlank(q("startLocation"), q("origin"));

            var endLocation = hasBody ? Pick(body, "endLocation", "destination") : "";
            if (string.IsNullOrWhiteSpace(endLocation))
                endLocation = FirstNonBlank(q("endLocation"), q("destination"));

            var weightText = hasBody ? Pick(body, "weight") : "";
            if (string.IsNullOrWhiteSpace(weightText))
                weightText = q("weight");

            double.TryParse(weightText, out var weight);

            LogLoadApi(logPath,
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {routeName} method={req.Method} raw={rawBody} parsedLoad={loadNumber} parsedDriver={driver} parsedTruck={truck} parsedCargo={cargo}");

            if (string.IsNullOrWhiteSpace(loadNumber))
                return null;

            return new LoadDto
            {
                LoadNumber = loadNumber,
                Driver = driver,
                Truck = truck,
                Cargo = cargo,
                Weight = weight,
                StartLocation = startLocation,
                EndLocation = endLocation
            };
        }
        catch (Exception ex)
        {
            LogLoadApi(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {routeName} exception={ex}");
            return null;
        }
    }

    private static void LogLoadApi(string path, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v))
            {
                var s = v.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return "";
    }

    private sealed class ThreadResult
    {
        public bool ThreadCreated { get; set; }
        public string ThreadId { get; set; } = "";
        public bool Archived { get; set; }
        public string Reason { get; set; } = "";
        public bool FallbackPosted { get; set; }
    }

    private static async Task<ThreadResult> PostLoadPickup(
        DiscordSocketClient? client,
        DispatchSettingsStore? dispatchStore,
        ProgramLoadThreadStore store,
        LoadDto dto,
        string logPath)
    {
        var result = new ThreadResult();

        if (client == null)
        {
            result.Reason = "ClientNull";
            return result;
        }

        if (dispatchStore == null)
        {
            result.Reason = "DispatchStoreNull";
            return result;
        }

        foreach (var guild in client.Guilds)
        {
            var settings = dispatchStore.Get(guild.Id.ToString());
            if (!ulong.TryParse(settings.DispatchChannelId, out var channelId) || channelId == 0)
                continue;

            var textChannel = guild.GetTextChannel(channelId);
            if (textChannel == null)
            {
                result.Reason = "DispatchChannelNotFound";
                continue;
            }

            try
            {
                var embed = BuildEmbed("Load Picked Up", dto);
                var rootMessage = await textChannel.SendMessageAsync(embed: embed);

                try
                {
                    var thread = await textChannel.CreateThreadAsync(
                        name: $"load-{Sanitize(dto.LoadNumber)}",
                        type: ThreadType.PublicThread,
                        autoArchiveDuration: ThreadArchiveDuration.OneDay,
                        message: rootMessage);

                    store.Upsert(new ProgramLoadThreadStore.LoadThreadEntry
                    {
                        LoadNumber = dto.LoadNumber ?? "",
                        ThreadId = thread.Id.ToString(),
                        ChannelId = channelId.ToString(),
                        GuildId = guild.Id.ToString()
                    });

                    await thread.SendMessageAsync(embed: BuildEmbed("In Transit", dto));

                    result.ThreadCreated = true;
                    result.ThreadId = thread.Id.ToString();
                    result.Reason = "OK";
                    return result;
                }
                catch (Exception threadEx)
                {
                    result.Reason = "ThreadCreateFailed: " + threadEx.Message;
                    LogLoadApi(logPath, $"pickup thread create failed load={dto.LoadNumber} guild={guild.Id} channel={channelId} ex={threadEx}");

                    await textChannel.SendMessageAsync($"Load `{dto.LoadNumber}` picked up by `{(dto.Driver ?? "Driver")}`.");
                    result.FallbackPosted = true;

                    store.Upsert(new ProgramLoadThreadStore.LoadThreadEntry
                    {
                        LoadNumber = dto.LoadNumber ?? "",
                        ThreadId = "",
                        ChannelId = channelId.ToString(),
                        GuildId = guild.Id.ToString(),
                        Archived = false
                    });

                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Reason = "ChannelPostFailed: " + ex.Message;
                LogLoadApi(logPath, $"pickup post failed load={dto.LoadNumber} guild={guild.Id} channel={channelId} ex={ex}");
                return result;
            }
        }

        if (string.IsNullOrWhiteSpace(result.Reason))
            result.Reason = "NoDispatchChannelConfigured";

        return result;
    }

    private static async Task<ThreadResult> PostLoadComplete(
        DiscordSocketClient? client,
        ProgramLoadThreadStore store,
        LoadDto dto,
        string logPath)
    {
        var result = new ThreadResult();

        if (client == null)
        {
            result.Reason = "ClientNull";
            return result;
        }

        var map = store.GetByLoadNumber(dto.LoadNumber ?? "");
        if (map == null)
        {
            result.Reason = "NoSavedLoadThread";
            return result;
        }

        if (ulong.TryParse(map.ThreadId, out var threadId) && threadId != 0)
        {
            try
            {
                var channel = await client.Rest.GetChannelAsync(threadId);
                if (channel is RestThreadChannel thread)
                {
                    await thread.SendMessageAsync(embed: BuildEmbed("Completed", dto));
                    await thread.ModifyAsync(x => x.Archived = true);
                    store.MarkArchived(dto.LoadNumber ?? "");
                    result.Archived = true;
                    result.Reason = "OK";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Reason = "ThreadArchiveFailed: " + ex.Message;
                LogLoadApi(logPath, $"complete thread archive failed load={dto.LoadNumber} thread={map.ThreadId} ex={ex}");
            }
        }

        if (ulong.TryParse(map.GuildId, out var guildId) &&
            ulong.TryParse(map.ChannelId, out var channelId))
        {
            try
            {
                var guild = client.GetGuild(guildId);
                var channel = guild?.GetTextChannel(channelId);
                if (channel != null)
                {
                    await channel.SendMessageAsync($"Load `{dto.LoadNumber}` completed by `{(dto.Driver ?? "Driver")}`.");
                    result.FallbackPosted = true;
                    result.Reason = string.IsNullOrWhiteSpace(result.Reason) ? "FallbackPosted" : result.Reason;
                    store.MarkArchived(dto.LoadNumber ?? "");
                    result.Archived = true;
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Reason = "FallbackCompletePostFailed: " + ex.Message;
                LogLoadApi(logPath, $"complete fallback post failed load={dto.LoadNumber} channel={map.ChannelId} ex={ex}");
                return result;
            }
        }

        if (string.IsNullOrWhiteSpace(result.Reason))
            result.Reason = "NoUsableThreadOrChannel";

        return result;
    }

    private static string Sanitize(string? value)
    {
        var s = (value ?? "load").Trim().ToLowerInvariant();
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "load" : safe;
    }

    private static Embed BuildEmbed(string title, LoadDto dto)
    {
        var eb = new EmbedBuilder()
            .WithTitle(title)
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(dto.LoadNumber))
            eb.AddField("Load", dto.LoadNumber, true);

        if (!string.IsNullOrWhiteSpace(dto.Driver))
            eb.AddField("Driver", dto.Driver, true);

        if (!string.IsNullOrWhiteSpace(dto.Truck))
            eb.AddField("Truck", dto.Truck, true);

        if (!string.IsNullOrWhiteSpace(dto.Cargo))
            eb.AddField("Cargo", dto.Cargo, false);

        if (dto.Weight > 0)
            eb.AddField("Weight", dto.Weight.ToString("N0"), true);

        if (!string.IsNullOrWhiteSpace(dto.StartLocation))
            eb.AddField("From", dto.StartLocation, true);

        eb.AddField("To", string.IsNullOrWhiteSpace(dto.EndLocation) ? "In Transit" : dto.EndLocation, true);

        return eb.Build();
    }

    private sealed class ProgramLoadThreadStore
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _opts;

        public ProgramLoadThreadStore(string path, JsonSerializerOptions opts)
        {
            _path = path;
            _opts = opts;
        }

        public sealed class LoadThreadEntry
        {
            public string LoadNumber { get; set; } = "";
            public string ThreadId { get; set; } = "";
            public string ChannelId { get; set; } = "";
            public string GuildId { get; set; } = "";
            public bool Archived { get; set; }
        }

        public List<LoadThreadEntry> Load()
        {
            if (!File.Exists(_path))
                return new List<LoadThreadEntry>();

            return JsonSerializer.Deserialize<List<LoadThreadEntry>>(File.ReadAllText(_path), _opts)
                   ?? new List<LoadThreadEntry>();
        }

        public void Save(List<LoadThreadEntry> list)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(list, _opts));
        }

        public void Upsert(LoadThreadEntry entry)
        {
            var list = Load();
            var existing = list.FirstOrDefault(x => string.Equals(x.LoadNumber, entry.LoadNumber, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
                list.Add(entry);
            else
            {
                existing.ThreadId = entry.ThreadId;
                existing.ChannelId = entry.ChannelId;
                existing.GuildId = entry.GuildId;
                existing.Archived = entry.Archived;
            }

            Save(list);
        }

        public LoadThreadEntry? GetByLoadNumber(string load)
        {
            return Load().FirstOrDefault(x => string.Equals(x.LoadNumber, load, StringComparison.OrdinalIgnoreCase));
        }

        public void MarkArchived(string load)
        {
            var list = Load();
            var existing = list.FirstOrDefault(x => string.Equals(x.LoadNumber, load, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.Archived = true;

            Save(list);
        }
    }
}
