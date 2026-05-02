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
                    points = Array.Empty<object>(),
                    source = "none"
                });
            }

            // Primary source: desktop ATS telemetry posted to /api/telemetry.
            var telemetryPoints = LoadTelemetryMapPointsFromDisk(dataDir, guildId);
            if (telemetryPoints.Count > 0)
            {
                return Results.Ok(new
                {
                    ok = true,
                    guildId,
                    count = telemetryPoints.Count,
                    points = telemetryPoints,
                    source = "telemetry"
                });
            }

            // Fallback source: older Discord driver status store.
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
                    lastSeenUtc = x.LastSeenUtc,
                    source = "driverStatus"
                })
                .Cast<object>()
                .ToList();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                count = points.Count,
                points,
                source = "driverStatus"
            });
        });

        app.MapGet("/api/vtc/live-summary", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                guildId = _client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

            var points = string.IsNullOrWhiteSpace(guildId)
                ? new List<object>()
                : LoadTelemetryMapPointsFromDisk(dataDir, guildId);

            var activeLoads = points.Count(x =>
            {
                var json = JsonSerializer.Serialize(x);
                return json.Contains("sourceCompany", StringComparison.OrdinalIgnoreCase) ||
                       json.Contains("destinationCompany", StringComparison.OrdinalIgnoreCase) ||
                       json.Contains("cargo", StringComparison.OrdinalIgnoreCase);
            });

            return Results.Ok(new
            {
                ok = true,
                guildId,
                activeDrivers = points.Count,
                activeLoads,
                updatedUtc = DateTimeOffset.UtcNow,
                points
            });
        });
    }

    private static List<object> LoadTelemetryMapPointsFromDisk(string dataDir, string guildId)
    {
        var result = new List<object>();

        try
        {
            var path = Path.Combine(dataDir, "live_telemetry.json");
            if (!File.Exists(path))
                return result;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            if (!doc.RootElement.TryGetProperty(guildId, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var unit in arr.EnumerateArray())
            {
                var updatedUtc = ReadDate(unit, "updatedUtc", "UpdatedUtc");
                if (updatedUtc.HasValue && (DateTimeOffset.UtcNow - updatedUtc.Value).TotalMinutes > 10)
                    continue;

                var lat = ReadDouble(unit, "latitude", "Latitude", "lat", "Lat");
                var lng = ReadDouble(unit, "longitude", "Longitude", "lng", "Lng", "lon", "Lon");

                if (!lat.HasValue || !lng.HasValue || (Math.Abs(lat.Value) < 0.000001 && Math.Abs(lng.Value) < 0.000001))
                    continue;

                var city = ReadString(unit, "city", "City");
                var state = ReadString(unit, "state", "State");

                result.Add(new
                {
                    discordUserId = ReadString(unit, "driverDiscordUserId", "DriverDiscordUserId", "discordUserId", "DiscordUserId"),
                    driverName = FirstTelemetryNonBlank(ReadString(unit, "driverName", "DriverName"), ReadString(unit, "driver", "Driver"), ReadString(unit, "discordUsername", "DiscordUsername"), "Driver"),
                    dutyStatus = FirstTelemetryNonBlank(ReadString(unit, "status", "Status", "dutyStatus", "DutyStatus"), "Live"),
                    truck = FirstTelemetryNonBlank(ReadString(unit, "truckName", "TruckName"), ReadString(unit, "truck", "Truck"), ReadString(unit, "truckNumber", "TruckNumber")),
                    truckNumber = ReadString(unit, "truckNumber", "TruckNumber"),
                    loadNumber = ReadString(unit, "loadNumber", "LoadNumber", "currentLoadNumber", "CurrentLoadNumber"),
                    cargo = ReadString(unit, "cargo", "Cargo", "cargoName", "CargoName"),
                    sourceCity = ReadString(unit, "sourceCity", "SourceCity", "pickupCity", "PickupCity"),
                    sourceCompany = ReadString(unit, "sourceCompany", "SourceCompany", "pickupCompany", "PickupCompany"),
                    destinationCity = ReadString(unit, "destinationCity", "DestinationCity", "dropCity", "DropCity"),
                    destinationCompany = ReadString(unit, "destinationCompany", "DestinationCompany", "dropCompany", "DropCompany"),
                    location = string.Join(", ", new[] { city, state }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    city,
                    state,
                    speedMph = ReadDouble(unit, "speedMph", "SpeedMph", "speed", "Speed"),
                    fuel = ReadDouble(unit, "fuel", "Fuel", "fuelPercent", "FuelPercent"),
                    damage = ReadDouble(unit, "damage", "Damage", "damagePercent", "DamagePercent"),
                    latitude = lat.Value,
                    longitude = lng.Value,
                    heading = ReadDouble(unit, "heading", "Heading", "headingDeg", "HeadingDeg"),
                    lastSeenUtc = updatedUtc,
                    updatedUtc,
                    source = "telemetry"
                });
            }
        }
        catch
        {
        }

        return result;
    }

    private static string? FirstTelemetryNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.String)
                return el.GetString();

            if (el.ValueKind == JsonValueKind.Number || el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
                return el.ToString();
        }

        return null;
    }

    private static double? ReadDouble(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;

            try
            {
                if (el.ValueKind == JsonValueKind.Number)
                    return el.GetDouble();

                if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var d))
                    return d;
            }
            catch { }
        }

        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement root, params string[] names)
    {
        var s = ReadString(root, names);
        return DateTimeOffset.TryParse(s, out var dto) ? dto : null;
    }

    
}
