using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Reflection;
using System.Text.Json;
using System.Net.Http.Json;
using Discord;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Routes;

public static class ManagementRoutes
{
    public static void Register(
        WebApplication app,
        BotServices services,
        DispatchMessageStore messageStore,
        DriverDisciplineStore disciplineStore)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        app.MapGet("/api/vtc/settings", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString();
            var path = Path.Combine(dataDir, $"settings_{guildId}.json");

            if (!File.Exists(path))
                return Results.Ok(new { ok = true, data = new { } });

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<object>(json);

            return Results.Ok(new { ok = true, data });
        });

        app.MapPost("/api/vtc/settings/update", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(ctx.Request.Body);
            if (body == null)
                return Results.BadRequest(new { ok = false });

            var guildId = Get(body, "guildId");
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var path = Path.Combine(dataDir, $"settings_{guildId}.json");

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));

            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/announcements", async (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString();

            var stored = LoadList(dataDir, $"announcements_{guildId}.json");
            var discord = await LoadDiscordAnnouncements(services, dataDir, guildId);

            var merged = stored.Concat(discord)
                .GroupBy(x => x.ContainsKey("id") ? x["id"]?.ToString() : "")
                .Select(g => g.First())
                .OrderByDescending(GetDate)
                .ToList();

            return Results.Ok(new { ok = true, data = merged });
        });

        app.MapPost("/api/announcements/create", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(ctx.Request.Body);
            if (body == null)
                return Results.BadRequest(new { ok = false });

            var guildId = Get(body, "guildId");
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            body["id"] = $"ANN-{DateTime.UtcNow:yyyyMMddHHmmss}";
            body["sentUtc"] = DateTime.UtcNow;

            var list = LoadList(dataDir, $"announcements_{guildId}.json");
            list.Insert(0, body);

            SaveList(dataDir, $"announcements_{guildId}.json", list);

            var discord = await PostAnnouncementToDiscord(services, dataDir, guildId, body);

            var postedProp = discord.GetType().GetProperty("posted");
            if (postedProp != null && postedProp.GetValue(discord) is bool posted && !posted)
            {
                return Results.Ok(new
                {
                    ok = false,
                    error = "DiscordPostFailed",
                    discord
                });
            }

            return Results.Ok(new { ok = true, discord });
        });

        app.MapGet("/api/bol/all", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString();
            var list = LoadList(dataDir, $"bol_{guildId}.json");
            return Results.Ok(new { ok = true, data = list });
        });

        app.MapPost("/api/bol/create", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(ctx.Request.Body);
            if (body == null)
                return Results.BadRequest(new { ok = false });

            var guildId = Get(body, "guildId");
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            body["bolId"] = $"BOL-{DateTime.UtcNow:yyyyMMddHHmmss}";
            body["createdUtc"] = DateTime.UtcNow;
            body["status"] = "Created";

            var list = LoadList(dataDir, $"bol_{guildId}.json");
            list.Insert(0, body);

            SaveList(dataDir, $"bol_{guildId}.json", list);

            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/management/summary", async (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var drivers = await BuildDriversAsync(services, dataDir, guildId, messageStore);

            var summary = new
            {
                guildId,
                drivers = drivers.Count,
                live = drivers.Count(IsLive),
                stale = drivers.Count(IsStale),
                inactive30 = drivers.Count(IsInactive30),
                inactive60 = drivers.Count(IsInactive60),
                unreadDispatch = drivers.Sum(x => x.UnreadDispatchCount),
                needsAttention = drivers.Count(x => AttentionScore(x) >= 2),
                recentConversations = messageStore.ListRecentConversations(guildId, 12)
            };

            return Results.Ok(new { ok = true, data = summary });
        });

        app.MapGet("/api/management/drivers", async (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var drivers = await BuildDriversAsync(services, dataDir, guildId, messageStore);

            return Results.Ok(new { ok = true, data = drivers });
        });

        app.MapGet("/api/management/drivers/{driverDiscordUserId}", async (
            HttpRequest req,
            string driverDiscordUserId) =>
        {
            var guildId = req.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.BadRequest(new { ok = false, error = "MissingDriverDiscordUserId" });

            var drivers = await BuildDriversAsync(services, dataDir, guildId, messageStore);
            var driver = drivers.FirstOrDefault(x =>
                string.Equals(x.DiscordUserId, driverDiscordUserId, StringComparison.OrdinalIgnoreCase));

            if (driver == null)
            {
                driver = new ManagementDriverDto
                {
                    DiscordUserId = driverDiscordUserId,
                    DiscordUsername = driverDiscordUserId,
                    Name = driverDiscordUserId,
                    DriverName = driverDiscordUserId
                };
            }

            var messages = messageStore.ListConversation(guildId, driverDiscordUserId)
                .Select(m => new
                {
                    id = m.Id,
                    guildId = m.GuildId,
                    driverDiscordUserId = m.DriverDiscordUserId,
                    driverName = m.DriverName,
                    text = m.Text,
                    direction = m.Direction,
                    isRead = m.IsRead,
                    createdUtc = m.CreatedUtc
                })
                .ToList();

            var notes = LoadDriverNote(dataDir, guildId, driverDiscordUserId);
            if (!string.IsNullOrWhiteSpace(notes))
                driver.Notes = notes;

            var payload = new
            {
                driver = new
                {
                    name = driver.Name,
                    driverName = driver.DriverName,
                    discordUserId = driver.DiscordUserId,
                    discordUsername = driver.DiscordUsername,
                    role = driver.Role,
                    truckNumber = driver.TruckNumber,
                    dutyStatus = driver.DutyStatus,
                    status = driver.Status,
                    location = driver.Location,
                    loadNumber = driver.LoadNumber,
                    lastSeenUtc = driver.LastSeenUtc,
                    unreadDispatchCount = driver.UnreadDispatchCount,
                    notes = driver.Notes
                },
                live = new
                {
                    dutyStatus = driver.DutyStatus,
                    location = driver.Location,
                    loadNumber = driver.LoadNumber,
                    speedMph = driver.SpeedMph,
                    lastSeenUtc = driver.LastSeenUtc
                },
                messages
            };

            return Results.Ok(new { ok = true, data = payload });
        });

        app.MapPost("/api/management/driver/role", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await ReadJsonBodyAsync(ctx);
            var guildId = Get(body, "guildId");
            var driverDiscordUserId = Get(body, "driverDiscordUserId");
            var role = Get(body, "role");

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.BadRequest(new { ok = false, error = "MissingDriverDiscordUserId" });
            if (string.IsNullOrWhiteSpace(role))
                return Results.BadRequest(new { ok = false, error = "MissingRole" });

            SaveDriverRole(dataDir, guildId, driverDiscordUserId, role);

            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/management/driver/role/bulk", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await ReadJsonBodyAsync(ctx);
            var guildId = Get(body, "guildId");
            var role = Get(body, "role");
            var ids = GetStringList(body, "driverDiscordUserIds");

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            if (string.IsNullOrWhiteSpace(role))
                return Results.BadRequest(new { ok = false, error = "MissingRole" });
            if (ids.Count == 0)
                return Results.BadRequest(new { ok = false, error = "NoDriversSelected" });

            foreach (var id in ids)
                SaveDriverRole(dataDir, guildId, id, role);

            return Results.Ok(new { ok = true, updated = ids.Count });
        });

        app.MapPost("/api/management/driver/note", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await ReadJsonBodyAsync(ctx);
            var guildId = Get(body, "guildId");
            var driverDiscordUserId = Get(body, "driverDiscordUserId");
            var notes = Get(body, "notes") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.BadRequest(new { ok = false, error = "MissingDriverDiscordUserId" });

            SaveDriverNote(dataDir, guildId, driverDiscordUserId, notes);

            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/management/message/fleet", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await ReadJsonBodyAsync(ctx);
            var guildId = Get(body, "guildId");
            var text = Get(body, "text");

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { ok = false, error = "MissingText" });

            var drivers = await BuildDriversAsync(services, dataDir, guildId, messageStore);

            var sent = 0;
            var failed = new List<object>();

            foreach (var driver in drivers.Where(x => !string.IsNullOrWhiteSpace(x.DiscordUserId)))
            {
                var driverId = driver.DiscordUserId?.Trim() ?? "";
                var driverName = driver.NameOrFallback();

                var send = await SendDispatchMessageAsync(
                    services,
                    dataDir,
                    guildId,
                    driverId,
                    driverName,
                    text);

                if (send.Success)
                {
                    messageStore.Add(new DispatchMessage
                    {
                        GuildId = guildId,
                        DriverDiscordUserId = driverId,
                        DriverName = driverName,
                        Direction = "outbound",
                        Text = text,
                        IsRead = true,
                        CreatedUtc = DateTimeOffset.UtcNow
                    });

                    sent++;
                }
                else
                {
                    failed.Add(new
                    {
                        driverDiscordUserId = driverId,
                        driverName,
                        error = send.Reason
                    });
                }
            }

            return Results.Ok(new
            {
                ok = sent > 0,
                sent,
                failedCount = failed.Count,
                failed
            });
        });

        app.MapPost("/api/management/message/selected", async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx))
                return Results.Unauthorized();

            var body = await ReadJsonBodyAsync(ctx);
            var guildId = Get(body, "guildId");
            var text = Get(body, "text");
            var ids = GetStringList(body, "driverDiscordUserIds");

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { ok = false, error = "MissingText" });
            if (ids.Count == 0)
                return Results.BadRequest(new { ok = false, error = "NoDriversSelected" });

            var drivers = await BuildDriversAsync(services, dataDir, guildId, messageStore);
            var lookup = drivers
                .Where(x => !string.IsNullOrWhiteSpace(x.DiscordUserId))
                .GroupBy(x => x.DiscordUserId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var sent = 0;
            var failed = new List<object>();

            foreach (var id in ids)
            {
                lookup.TryGetValue(id, out var driver);
                var name = driver?.NameOrFallback() ?? id;

                var send = await SendDispatchMessageAsync(
                    services,
                    dataDir,
                    guildId,
                    id,
                    name,
                    text);

                if (send.Success)
                {
                    messageStore.Add(new DispatchMessage
                    {
                        GuildId = guildId,
                        DriverDiscordUserId = id,
                        DriverName = name,
                        Direction = "outbound",
                        Text = text,
                        IsRead = true,
                        CreatedUtc = DateTimeOffset.UtcNow
                    });

                    sent++;
                }
                else
                {
                    failed.Add(new
                    {
                        driverDiscordUserId = id,
                        driverName = name,
                        error = send.Reason
                    });
                }
            }

            return Results.Ok(new
            {
                ok = sent > 0,
                sent,
                failedCount = failed.Count,
                failed
            });
        });
    }

    private static async Task<List<ManagementDriverDto>> BuildDriversAsync(
        BotServices services,
        string dataDir,
        string guildId,
        DispatchMessageStore messageStore)
    {
        var roleMap = LoadStringMap(dataDir, $"driver_roles_{guildId}.json");
        var noteMap = LoadStringMap(dataDir, $"driver_notes_{guildId}.json");
        var liveMap = await LoadLiveTelemetryMapAsync(dataDir, guildId);
        var convoMap = messageStore.ListRecentConversations(guildId, 500)
            .GroupBy(x => x.DriverDiscordUserId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var drivers = new Dictionary<string, ManagementDriverDto>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (ulong.TryParse(guildId, out var guildUlong))
            {
                var guild = services.Client.GetGuild(guildUlong);
                if (guild != null)
                {
                    foreach (var user in guild.Users)
                    {
                        var id = user.Id.ToString();
                        if (!drivers.ContainsKey(id))
                        {
                            drivers[id] = new ManagementDriverDto
                            {
                                DiscordUserId = id,
                                DiscordUsername = user.Username,
                                Name = !string.IsNullOrWhiteSpace(user.Nickname) ? user.Nickname : user.Username,
                                DriverName = !string.IsNullOrWhiteSpace(user.Nickname) ? user.Nickname : user.Username,
                                Role = ResolveRoleForUser(user, roleMap, id),
                                Status = user.Status.ToString(),
                                LastSeenUtc = null
                            };
                        }
                    }
                }
            }
        }
        catch
        {
        }

        foreach (var kvp in convoMap)
        {
            var id = kvp.Key;
            var convo = kvp.Value;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (!drivers.TryGetValue(id, out var d))
            {
                d = new ManagementDriverDto
                {
                    DiscordUserId = id,
                    DiscordUsername = id,
                    Name = !string.IsNullOrWhiteSpace(convo.DriverName) ? convo.DriverName : id,
                    DriverName = !string.IsNullOrWhiteSpace(convo.DriverName) ? convo.DriverName : id,
                    Role = GetRole(roleMap, id)
                };
                drivers[id] = d;
            }

            d.UnreadDispatchCount = convo.UnreadCount;
            if (d.LastSeenUtc == null)
                d.LastSeenUtc = convo.LastCreatedUtc;
        }

        foreach (var kvp in liveMap)
        {
            var id = kvp.Key;
            var live = kvp.Value;

            if (!drivers.TryGetValue(id, out var d))
            {
                d = new ManagementDriverDto
                {
                    DiscordUserId = id,
                    DiscordUsername = GetJsonString(live, "discordUsername") ?? id,
                    Name = GetJsonString(live, "name") ?? GetJsonString(live, "driverName") ?? id,
                    DriverName = GetJsonString(live, "driverName") ?? GetJsonString(live, "name") ?? id,
                    Role = GetRole(roleMap, id)
                };
                drivers[id] = d;
            }

            d.TruckNumber = FirstNonBlank(
                GetJsonString(live, "truckNumber"),
                GetJsonString(live, "truck"),
                d.TruckNumber);

            d.DutyStatus = FirstNonBlank(
                GetJsonString(live, "dutyStatus"),
                GetJsonString(live, "duty"),
                d.DutyStatus);

            d.Location = FirstNonBlank(
                GetJsonString(live, "location"),
                GetJsonString(live, "city"),
                d.Location);

            d.LoadNumber = FirstNonBlank(
                GetJsonString(live, "loadNumber"),
                GetJsonString(live, "load"),
                d.LoadNumber);

            d.Status = FirstNonBlank(
                GetJsonString(live, "status"),
                d.Status);

            d.LastSeenUtc = GetJsonDate(live, "lastSeenUtc") ?? d.LastSeenUtc;
            d.SpeedMph = GetJsonDouble(live, "speedMph") ?? d.SpeedMph;
        }

        foreach (var d in drivers.Values)
        {
            d.Role = FirstNonBlank(GetRole(roleMap, d.DiscordUserId), d.Role, "Driver");
            d.Notes = FirstNonBlank(GetNote(noteMap, d.DiscordUserId), d.Notes);
        }

        return drivers.Values
            .OrderByDescending(AttentionScore)
            .ThenBy(x => x.NameOrFallback(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<Dictionary<string, JsonElement>> LoadLiveTelemetryMapAsync(string dataDir, string guildId)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var candidates = new[]
        {
            Path.Combine(dataDir, $"driver_live_{guildId}.json"),
            Path.Combine(dataDir, $"drivers_live_{guildId}.json"),
            Path.Combine(dataDir, $"fleet_live_{guildId}.json"),
            Path.Combine(dataDir, $"telemetry_{guildId}.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        var id = FirstNonBlank(
                            GetJsonString(item, "discordUserId"),
                            GetJsonString(item, "driverDiscordUserId"));

                        if (!string.IsNullOrWhiteSpace(id))
                            result[id] = item.Clone();
                    }

                    if (result.Count > 0)
                        return result;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in drivers.EnumerateArray())
                        {
                            var id = FirstNonBlank(
                                GetJsonString(item, "discordUserId"),
                                GetJsonString(item, "driverDiscordUserId"));

                            if (!string.IsNullOrWhiteSpace(id))
                                result[id] = item.Clone();
                        }

                        if (result.Count > 0)
                            return result;
                    }

                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            var id = FirstNonBlank(
                                GetJsonString(item, "discordUserId"),
                                GetJsonString(item, "driverDiscordUserId"));

                            if (!string.IsNullOrWhiteSpace(id))
                                result[id] = item.Clone();
                        }

                        if (result.Count > 0)
                            return result;
                    }
                }
            }
            catch
            {
            }
        }

        return result;
    }

    private static async Task<List<Dictionary<string, object>>> LoadDiscordAnnouncements(
        BotServices services,
        string dataDir,
        string guildId)
    {
        try
        {
            var channel = await ResolveDiscordTargetChannelAsync(
                services,
                dataDir,
                guildId,
                preferAnnouncement: true);

            if (channel is not SocketTextChannel textChannel)
                return new();

            var messages = await textChannel.GetMessagesAsync(100).FlattenAsync();
            var list = new List<Dictionary<string, object>>();

            foreach (var m in messages.OrderByDescending(x => x.Timestamp.UtcDateTime))
            {
                var title = "";
                var body = "";

                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    var parts = m.Content.Split('\n', 2);
                    title = parts.FirstOrDefault()?.Trim() ?? "";
                    body = m.Content.Trim();
                }

                if ((string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) && m.Embeds != null && m.Embeds.Count > 0)
                {
                    var embed = m.Embeds.First();
                    title = string.IsNullOrWhiteSpace(title) ? (embed.Title ?? "Announcement") : title;

                    if (string.IsNullOrWhiteSpace(body))
                    {
                        body = embed.Description ?? "";

                        if (string.IsNullOrWhiteSpace(body) && embed.Fields != null && embed.Fields.Any())
                        {
                            body = string.Join("\n", embed.Fields.Select(f => $"{f.Name}: {f.Value}"));
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                    continue;

                list.Add(new Dictionary<string, object>
                {
                    ["id"] = $"DISCORD-{m.Id}",
                    ["title"] = string.IsNullOrWhiteSpace(title) ? "Announcement" : title,
                    ["body"] = body,
                    ["sentUtc"] = m.Timestamp.UtcDateTime,
                    ["source"] = "discord"
                });
            }

            return list;
        }
        catch
        {
            return new();
        }
    }

    private static async Task<object> PostAnnouncementToDiscord(
        BotServices services,
        string dataDir,
        string guildId,
        Dictionary<string, object> body)
    {
        try
        {
            var settings = services.DispatchStore?.Get(guildId);

            var title = Get(body, "title") ?? "Announcement";
            var text = Get(body, "body") ?? "";
            var msg = $"📢 **{title}**\n\n{text}";

            if (!string.IsNullOrWhiteSpace(settings?.AnnouncementWebhookUrl))
            {
                using var http = new HttpClient();
                var payload = new { content = msg };

                var res = await http.PostAsJsonAsync(settings.AnnouncementWebhookUrl, payload);

                return new
                {
                    posted = res.IsSuccessStatusCode,
                    via = "webhook",
                    status = res.StatusCode.ToString()
                };
            }

            var channel = await ResolveDiscordTargetChannelAsync(
                services,
                dataDir,
                guildId,
                preferAnnouncement: true);

            if (channel is not IMessageChannel messageChannel)
                return new { posted = false, reason = "ChannelNotFound" };

            var sent = await messageChannel.SendMessageAsync(msg);

            return new
            {
                posted = true,
                via = "channel",
                messageId = sent.Id
            };
        }
        catch (Exception ex)
        {
            return new
            {
                posted = false,
                error = ex.Message
            };
        }
    }

    private static async Task<(bool Success, string Reason)> SendDispatchMessageAsync(
        BotServices services,
        string dataDir,
        string guildId,
        string driverDiscordUserId,
        string driverName,
        string text)
    {
        try
        {
            if (services.Client == null)
                return (false, "ClientNull");

            if (string.IsNullOrWhiteSpace(guildId))
                return (false, "MissingGuildId");

            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return (false, "MissingDriverDiscordUserId");

            if (string.IsNullOrWhiteSpace(text))
                return (false, "MissingText");

            var guild = DiscordThreadService.ResolveGuild(services.Client, guildId);
            if (guild == null)
                return (false, "GuildNotFound");

            if (!ulong.TryParse(driverDiscordUserId, out var driverUserId) || driverUserId == 0)
                return (false, "InvalidDriverDiscordUserId");

            var displayName = string.IsNullOrWhiteSpace(driverName) ? driverDiscordUserId : driverName.Trim();

            var threadId = await DiscordThreadService.EnsureDriverThreadAsync(
                services.DispatchStore,
                services.ThreadStore,
                guild,
                driverUserId,
                displayName);

            if (threadId != 0)
            {
                var threadChannel = await DiscordThreadService.ResolveChannelAsync(services.Client, threadId);
                if (threadChannel != null)
                {
                    await DiscordThreadService.EnsureThreadOpenAsync(threadChannel);
                    await threadChannel.SendMessageAsync($"📨 **Dispatch for {displayName}**\n\n{text}");
                    return (true, "Thread");
                }
            }

            if (!string.IsNullOrWhiteSpace(services.DispatchStore?.Get(guildId)?.DispatchWebhookUrl))
            {
                using var http = new HttpClient();
                var payload = new { content = $"📨 **Dispatch for {displayName}**\n\n{text}" };

                var res = await http.PostAsJsonAsync(services.DispatchStore.Get(guildId).DispatchWebhookUrl, payload);
                if (res.IsSuccessStatusCode)
                    return (true, "Webhook");
            }

            var fallbackChannel = await ResolveDiscordTargetChannelAsync(
                services,
                dataDir,
                guildId,
                preferAnnouncement: false);

            if (fallbackChannel is IMessageChannel messageChannel)
            {
                await messageChannel.SendMessageAsync($"📨 **Dispatch for {displayName}**\n\n{text}");
                return (true, "FallbackChannel");
            }

            return (false, "DispatchChannelNotFound");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<IChannel?> ResolveDiscordTargetChannelAsync(
        BotServices services,
        string dataDir,
        string guildId,
        bool preferAnnouncement)
    {
        var dispatchStoreIds = ReadDispatchStoreChannelIds(services.DispatchStore, guildId);

        var preferredStoreId = preferAnnouncement
            ? dispatchStoreIds.AnnouncementChannelId ?? dispatchStoreIds.DispatchChannelId
            : dispatchStoreIds.DispatchChannelId ?? dispatchStoreIds.AnnouncementChannelId;

        if (preferredStoreId != null)
        {
            var ch = services.Client.GetChannel(preferredStoreId.Value);
            if (ch != null)
                return ch;
        }

        var settingsIds = await ReadSettingsDiscordAsync(dataDir, guildId);

        var preferredSettingsId = preferAnnouncement
            ? settingsIds.AnnouncementsChannelId ?? settingsIds.DispatchChannelId
            : settingsIds.DispatchChannelId ?? settingsIds.AnnouncementsChannelId;

        if (preferredSettingsId != null)
        {
            var ch = services.Client.GetChannel(preferredSettingsId.Value);
            if (ch != null)
                return ch;
        }

        if (!ulong.TryParse(guildId, out var guildUlong))
            return null;

        var guild = services.Client.GetGuild(guildUlong);
        if (guild == null)
            return null;

        var candidates = preferAnnouncement
            ? new[] { "announcements", "company-announcements", "vtc-announcements", "dispatch-center", "dispatch" }
            : new[] { "dispatch-center", "dispatch", "dispatch-board", "announcements" };

        foreach (var name in candidates)
        {
            var textChannel = guild.TextChannels.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (textChannel != null)
                return textChannel;
        }

        return null;
    }

    private static (ulong? AnnouncementChannelId, ulong? DispatchChannelId) ReadDispatchStoreChannelIds(
        DispatchSettingsStore? store,
        string guildId)
    {
        if (store == null || string.IsNullOrWhiteSpace(guildId))
            return (null, null);

        try
        {
            var settings = store.Get(guildId);
            if (settings == null)
                return (null, null);

            ulong? dispatch = TryReadUlongProperty(settings, "DispatchChannelId");
            ulong? announcement = TryReadUlongProperty(settings, "AnnouncementChannelId")
                                  ?? TryReadUlongProperty(settings, "AnnouncementsChannelId");

            return (announcement, dispatch);
        }
        catch
        {
            return (null, null);
        }
    }

    private static ulong? TryReadUlongProperty(object obj, string propertyName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
                return null;

            var raw = prop.GetValue(obj);
            if (raw == null)
                return null;

            if (raw is ulong u)
                return u > 0 ? u : null;

            var s = raw.ToString();
            if (ulong.TryParse(s, out var parsed) && parsed > 0)
                return parsed;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(ulong? AnnouncementsChannelId, ulong? DispatchChannelId)> ReadSettingsDiscordAsync(
        string dataDir,
        string guildId)
    {
        var path = Path.Combine(dataDir, $"settings_{guildId}.json");
        if (!File.Exists(path))
            return (null, null);

        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("discord", out var d))
                return (null, null);

            ulong? ann = null;
            ulong? dispatch = null;

            if (d.TryGetProperty("announcementsChannelId", out var a) &&
                ulong.TryParse(a.ToString(), out var annId))
            {
                ann = annId;
            }

            if (d.TryGetProperty("announcementChannelId", out var a2) &&
                ulong.TryParse(a2.ToString(), out var annId2))
            {
                ann ??= annId2;
            }

            if (d.TryGetProperty("dispatchChannelId", out var b) &&
                ulong.TryParse(b.ToString(), out var dispatchId))
            {
                dispatch = dispatchId;
            }

            return (ann, dispatch);
        }
        catch
        {
            return (null, null);
        }
    }

    private static void SaveDriverRole(string dataDir, string guildId, string driverDiscordUserId, string role)
    {
        var map = LoadStringMap(dataDir, $"driver_roles_{guildId}.json");
        map[driverDiscordUserId] = role;
        SaveStringMap(dataDir, $"driver_roles_{guildId}.json", map);
    }

    private static string GetRole(Dictionary<string, string> map, string driverDiscordUserId)
    {
        return map.TryGetValue(driverDiscordUserId, out var role) ? role : "";
    }

    private static void SaveDriverNote(string dataDir, string guildId, string driverDiscordUserId, string notes)
    {
        var map = LoadStringMap(dataDir, $"driver_notes_{guildId}.json");
        map[driverDiscordUserId] = notes ?? string.Empty;
        SaveStringMap(dataDir, $"driver_notes_{guildId}.json", map);
    }

    private static string LoadDriverNote(string dataDir, string guildId, string driverDiscordUserId)
    {
        var map = LoadStringMap(dataDir, $"driver_notes_{guildId}.json");
        return map.TryGetValue(driverDiscordUserId, out var notes) ? notes : "";
    }

    private static string GetNote(Dictionary<string, string> map, string driverDiscordUserId)
    {
        return map.TryGetValue(driverDiscordUserId, out var notes) ? notes : "";
    }

    private static string ResolveRoleForUser(SocketGuildUser user, Dictionary<string, string> map, string driverDiscordUserId)
    {
        var saved = GetRole(map, driverDiscordUserId);
        if (!string.IsNullOrWhiteSpace(saved))
            return saved;

        var roleNames = user.Roles
            .Where(r => r != null && !r.IsEveryone)
            .Select(r => r.Name)
            .ToList();

        if (roleNames.Any(x => x.Contains("owner", StringComparison.OrdinalIgnoreCase)))
            return "Owner";
        if (roleNames.Any(x => x.Contains("admin", StringComparison.OrdinalIgnoreCase)))
            return "Admin";
        if (roleNames.Any(x => x.Contains("manager", StringComparison.OrdinalIgnoreCase)))
            return "Manager";
        if (roleNames.Any(x => x.Contains("dispatch", StringComparison.OrdinalIgnoreCase)))
            return "Dispatch";

        return "Driver";
    }

    private static async Task<Dictionary<string, object>> ReadJsonBodyAsync(HttpContext ctx)
    {
        return await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(ctx.Request.Body)
               ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> GetStringList(Dictionary<string, object> body, string key)
    {
        if (!body.TryGetValue(key, out var raw) || raw == null)
            return new List<string>();

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                return je.EnumerateArray()
                    .Select(x => x.ToString().Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (je.ValueKind == JsonValueKind.String)
            {
                var s = je.GetString();
                if (string.IsNullOrWhiteSpace(s))
                    return new List<string>();

                return new List<string> { s.Trim() };
            }
        }

        var asString = raw.ToString();
        if (!string.IsNullOrWhiteSpace(asString))
            return new List<string> { asString.Trim() };

        return new List<string>();
    }

    private static List<Dictionary<string, object>> LoadList(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return new();

        return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            File.ReadAllText(path)) ?? new();
    }

    private static void SaveList(string dir, string file, List<Dictionary<string, object>> list)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, string> LoadStringMap(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                       File.ReadAllText(path))
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveStringMap(string dir, string file, Dictionary<string, string> map)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Get(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null)
            return null;

        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
                return je.GetString();

            return je.ToString();
        }

        return v.ToString();
    }

    private static DateTime GetDate(Dictionary<string, object> d)
    {
        return DateTime.TryParse(Get(d, "sentUtc"), out var dt) ? dt : DateTime.MinValue;
    }

    private static bool IsAuthorized(HttpContext ctx)
    {
        if (AuthGuard.IsLoggedIn(ctx))
        {
            string guildId = "";

            if (ctx.Request.Query.TryGetValue("guildId", out var qv))
                guildId = (qv.ToString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId))
            {
                try
                {
                    ctx.Request.EnableBuffering();

                    using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                    var raw = reader.ReadToEnd();
                    ctx.Request.Body.Position = 0;

                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("guildId", out var p))
                        {
                            guildId = (p.ToString() ?? "").Trim();
                        }
                    }
                }
                catch
                {
                    try { ctx.Request.Body.Position = 0; } catch { }
                }
            }

            if (!string.IsNullOrWhiteSpace(guildId) && AuthGuard.CanManageGuild(ctx, guildId))
                return true;
        }

        var key = Environment.GetEnvironmentVariable("MANAGEMENT_API_KEY");
        var header = ctx.Request.Headers["X-API-Key"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(key) && key == header;
    }

    private static string? GetJsonString(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.TryGetProperty(propertyName, out var p))
            return null;

        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static DateTimeOffset? GetJsonDate(JsonElement item, string propertyName)
    {
        var value = GetJsonString(item, propertyName);
        if (DateTimeOffset.TryParse(value, out var dto))
            return dto;

        return null;
    }

    private static double? GetJsonDouble(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.TryGetProperty(propertyName, out var p))
            return null;

        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d))
            return d;

        if (double.TryParse(p.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static bool IsLive(ManagementDriverDto driver)
    {
        if (driver.LastSeenUtc == null) return false;
        return (DateTimeOffset.UtcNow - driver.LastSeenUtc.Value).TotalMinutes < 5;
    }

    private static bool IsStale(ManagementDriverDto driver)
    {
        if (driver.LastSeenUtc == null) return true;
        return (DateTimeOffset.UtcNow - driver.LastSeenUtc.Value).TotalMinutes >= 5;
    }

    private static bool IsInactive30(ManagementDriverDto driver)
    {
        if (driver.LastSeenUtc == null) return false;
        return (DateTimeOffset.UtcNow - driver.LastSeenUtc.Value).TotalMinutes >= 30;
    }

    private static bool IsInactive60(ManagementDriverDto driver)
    {
        if (driver.LastSeenUtc == null) return false;
        return (DateTimeOffset.UtcNow - driver.LastSeenUtc.Value).TotalMinutes >= 60;
    }

    private static double AttentionScore(ManagementDriverDto driver)
    {
        double score = 0;

        if (IsInactive60(driver)) score += 3;
        else if (IsInactive30(driver)) score += 2;
        else if (IsStale(driver)) score += 1;

        if (driver.UnreadDispatchCount >= 3) score += 2;
        else if (driver.UnreadDispatchCount > 0) score += 1;

        if (string.IsNullOrWhiteSpace(driver.LoadNumber)) score += 0.5;

        return score;
    }

    private sealed class ManagementDriverDto
    {
        public string DiscordUserId { get; set; } = "";
        public string DiscordUsername { get; set; } = "";
        public string Name { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string Role { get; set; } = "Driver";
        public string TruckNumber { get; set; } = "";
        public string DutyStatus { get; set; } = "";
        public string Status { get; set; } = "";
        public string Location { get; set; } = "";
        public string LoadNumber { get; set; } = "";
        public int UnreadDispatchCount { get; set; }
        public DateTimeOffset? LastSeenUtc { get; set; }
        public string Notes { get; set; } = "";
        public double? SpeedMph { get; set; }

        public string NameOrFallback()
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (!string.IsNullOrWhiteSpace(DriverName)) return DriverName;
            if (!string.IsNullOrWhiteSpace(DiscordUsername)) return DiscordUsername;
            if (!string.IsNullOrWhiteSpace(DiscordUserId)) return DiscordUserId;
            return "Driver";
        }
    }
}
