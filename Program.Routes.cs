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
                    source = "none",
                    points = Array.Empty<object>(),
                    telemetryCount = 0,
                    statusCount = 0,
                    message = "Missing guildId"
                });
            }

            var points = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var telemetryCount = 0;
            var statusCount = 0;

            try
            {
                var telemetryFile = Path.Combine(dataDir, "live_telemetry.json");
                if (!File.Exists(telemetryFile))
                {
                    var fallbackFile = Path.Combine(AppContext.BaseDirectory, "data", "live_telemetry.json");
                    if (File.Exists(fallbackFile))
                        telemetryFile = fallbackFile;
                }

                if (File.Exists(telemetryFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(telemetryFile));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty(guildId, out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            telemetryCount++;

                            var latitude = ReadJsonDouble(el, "Latitude", "latitude", "Lat", "lat");
                            var longitude = ReadJsonDouble(el, "Longitude", "longitude", "Lng", "lng", "Lon", "lon");

                            if (!IsValidMapPoint(latitude, longitude))
                                continue;

                            var lastSeenUtc = ReadJsonDate(el, "UpdatedUtc", "updatedUtc", "LastSeenUtc", "lastSeenUtc");
                            if ((DateTimeOffset.UtcNow - lastSeenUtc).TotalMinutes > 30)
                                continue;

                            var driverId = ReadJsonString(el, "DriverDiscordUserId", "driverDiscordUserId", "DiscordUserId", "discordUserId", "UserId", "userId");
                            if (string.IsNullOrWhiteSpace(driverId))
                                driverId = ReadJsonString(el, "Driver", "driver", "DriverName", "driverName", "TruckName", "truckName") ?? Guid.NewGuid().ToString("N");

                            seen.Add(driverId);

                            var city = ReadJsonString(el, "City", "city") ?? "";
                            var state = ReadJsonString(el, "State", "state") ?? "";
                            var location = string.Join(", ", new[] { city, state }.Where(x => !string.IsNullOrWhiteSpace(x)));

                            points.Add(new
                            {
                                source = "telemetry",
                                discordUserId = driverId,
                                driverName = ReadJsonString(el, "DriverName", "driverName", "Driver", "driver", "DiscordUsername", "discordUsername") ?? "Driver",
                                dutyStatus = ReadJsonString(el, "Status", "status", "DutyStatus", "dutyStatus") ?? "Driving",
                                truck = ReadJsonString(el, "TruckName", "truckName", "Truck", "truck", "TruckNumber", "truckNumber") ?? "",
                                truckNumber = ReadJsonString(el, "TruckNumber", "truckNumber") ?? "",
                                loadNumber = ReadJsonString(el, "LoadNumber", "loadNumber", "CurrentLoadNumber", "currentLoadNumber") ?? "",
                                location,
                                speedMph = ReadJsonDouble(el, "SpeedMph", "speedMph", "SpeedMPH", "speedMPH", "Speed", "speed") ?? 0,
                                latitude,
                                longitude,
                                heading = ReadJsonDouble(el, "Heading", "heading") ?? 0,
                                conversionMode = ReadJsonString(el, "ConversionMode", "conversionMode") ?? "",
                                lastSeenUtc
                            });
                        }
                    }
                }
            }
            catch
            {
                // Telemetry fallback below keeps the map alive if the file is corrupt or unavailable.
            }

            try
            {
                var rows = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();
                statusCount = rows.Count;

                foreach (var x in rows)
                {
                    if (!IsValidMapPoint(x.Latitude, x.Longitude))
                        continue;

                    if (!string.IsNullOrWhiteSpace(x.DiscordUserId) && seen.Contains(x.DiscordUserId))
                        continue;

                    if ((DateTimeOffset.UtcNow - x.LastSeenUtc).TotalMinutes > 30)
                        continue;

                    points.Add(new
                    {
                        source = "driver-status",
                        discordUserId = x.DiscordUserId,
                        driverName = x.DriverName,
                        dutyStatus = x.DutyStatus,
                        truck = x.Truck,
                        truckNumber = "",
                        loadNumber = x.LoadNumber,
                        location = x.Location,
                        speedMph = x.SpeedMph,
                        latitude = x.Latitude,
                        longitude = x.Longitude,
                        heading = x.Heading,
                        conversionMode = "GPS",
                        lastSeenUtc = x.LastSeenUtc
                    });
                }
            }
            catch
            {
            }

            return Results.Ok(new
            {
                ok = true,
                guildId,
                source = points.Count > 0 ? "telemetry-or-status" : "none",
                count = points.Count,
                telemetryCount,
                statusCount,
                points
            });

            static bool IsValidMapPoint(double? latitude, double? longitude)
            {
                return latitude.HasValue && longitude.HasValue &&
                       double.IsFinite(latitude.Value) && double.IsFinite(longitude.Value) &&
                       Math.Abs(latitude.Value) <= 90 && Math.Abs(longitude.Value) <= 180 &&
                       (Math.Abs(latitude.Value) > 0.000001 || Math.Abs(longitude.Value) > 0.000001);
            }

            static string? ReadJsonString(JsonElement el, params string[] names)
            {
                foreach (var name in names)
                {
                    if (!el.TryGetProperty(name, out var value))
                        continue;

                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s.Trim();
                    }
                    else if (value.ValueKind == JsonValueKind.Number ||
                             value.ValueKind == JsonValueKind.True ||
                             value.ValueKind == JsonValueKind.False)
                    {
                        var s = value.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s.Trim();
                    }
                }

                return null;
            }

            static double? ReadJsonDouble(JsonElement el, params string[] names)
            {
                foreach (var name in names)
                {
                    if (!el.TryGetProperty(name, out var value))
                        continue;

                    try
                    {
                        if (value.ValueKind == JsonValueKind.Number)
                            return value.GetDouble();

                        if (value.ValueKind == JsonValueKind.String &&
                            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            return d;
                        }
                    }
                    catch
                    {
                    }
                }

                return null;
            }

            static DateTimeOffset ReadJsonDate(JsonElement el, params string[] names)
            {
                var s = ReadJsonString(el, names);
                return DateTimeOffset.TryParse(s, out var dto) ? dto : DateTimeOffset.UtcNow;
            }
        });
    }
}
