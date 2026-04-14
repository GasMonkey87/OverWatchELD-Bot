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
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot;

public static partial class Program
{
    private static void RegisterProgramRoutes(WebApplication app, BotServices services, string dataDir)
    {
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
    }
}
