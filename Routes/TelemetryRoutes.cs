using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace OverWatchELD.VtcBot.Routes;

public static class TelemetryRoutes
{
    private static readonly Dictionary<string, List<TelemetryUnit>> Units = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LockObj = new();

    public static WebApplication MapTelemetryRoutes(this WebApplication app)
    {
        app.MapGet("/api/telemetry", ([FromQuery] string? guildId) =>
        {
            LoadFromDisk();

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Ok(new { ok = true, data = Array.Empty<TelemetryUnit>(), count = 0, warning = "MissingGuildId" });

            guildId = guildId.Trim();

            lock (LockObj)
            {
                if (!Units.TryGetValue(guildId, out var units))
                    units = new List<TelemetryUnit>();

                units.RemoveAll(x => (DateTimeOffset.UtcNow - x.UpdatedUtc).TotalMinutes > 10);

                return Results.Ok(new
                {
                    ok = true,
                    guildId,
                    count = units.Count,
                    data = units.OrderByDescending(x => x.UpdatedUtc).ToList()
                });
            }
        });

        app.MapPost("/api/telemetry", async (HttpRequest req) =>
        {
            TelemetryUnit? unit;

            try
            {
                unit = await ReadTelemetryUnitAsync(req);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    ok = false,
                    error = "BadJson",
                    hint = "Expected telemetry JSON body.",
                    detail = ex.Message
                });
            }

            var queryGuildId = FirstNonBlank(req.Query["guildId"].ToString(), req.Query["serverId"].ToString());

            if (!string.IsNullOrWhiteSpace(queryGuildId))
                unit.GuildId = queryGuildId.Trim();

            if (string.IsNullOrWhiteSpace(unit.GuildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            if (string.IsNullOrWhiteSpace(unit.DriverDiscordUserId))
            {
                unit.DriverDiscordUserId = FirstNonBlank(
                    unit.DriverDiscordUserId,
                    unit.DiscordUserId,
                    unit.UserId,
                    unit.Driver,
                    unit.DriverName,
                    unit.Truck,
                    unit.TruckName,
                    Guid.NewGuid().ToString("N")
                )!;
            }

            unit.GuildId = unit.GuildId.Trim();
            unit.DriverDiscordUserId = unit.DriverDiscordUserId.Trim();
            unit.UpdatedUtc = DateTimeOffset.UtcNow;

            NormalizeCoordinates(unit);

            lock (LockObj)
            {
                if (!Units.TryGetValue(unit.GuildId, out var list))
                {
                    list = new List<TelemetryUnit>();
                    Units[unit.GuildId] = list;
                }

                list.RemoveAll(x =>
                    string.Equals(x.DriverDiscordUserId, unit.DriverDiscordUserId, StringComparison.OrdinalIgnoreCase));

                list.Add(unit);
                list.RemoveAll(x => (DateTimeOffset.UtcNow - x.UpdatedUtc).TotalMinutes > 10);

                SaveToDiskUnsafe();
            }

            return Results.Ok(new
            {
                ok = true,
                stored = true,
                guildId = unit.GuildId,
                driverDiscordUserId = unit.DriverDiscordUserId,
                longitude = unit.Longitude,
                latitude = unit.Latitude,
                conversionMode = unit.ConversionMode,
                data = unit
            });
        });

        app.MapPost("/api/telemetry/live", async (HttpRequest req) =>
        {
            TelemetryUnit? unit;

            try
            {
                unit = await ReadTelemetryUnitAsync(req);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = "BadJson", detail = ex.Message });
            }

            var queryGuildId = FirstNonBlank(req.Query["guildId"].ToString(), req.Query["serverId"].ToString());

            if (!string.IsNullOrWhiteSpace(queryGuildId))
                unit.GuildId = queryGuildId.Trim();

            if (string.IsNullOrWhiteSpace(unit.GuildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            if (string.IsNullOrWhiteSpace(unit.DriverDiscordUserId))
                unit.DriverDiscordUserId = FirstNonBlank(unit.Driver, unit.DriverName, unit.Truck, Guid.NewGuid().ToString("N"))!;

            unit.UpdatedUtc = DateTimeOffset.UtcNow;
            NormalizeCoordinates(unit);

            lock (LockObj)
            {
                if (!Units.TryGetValue(unit.GuildId, out var list))
                {
                    list = new List<TelemetryUnit>();
                    Units[unit.GuildId] = list;
                }

                list.RemoveAll(x => string.Equals(x.DriverDiscordUserId, unit.DriverDiscordUserId, StringComparison.OrdinalIgnoreCase));
                list.Add(unit);
                SaveToDiskUnsafe();
            }

            return Results.Ok(new { ok = true, data = unit });
        });

        app.MapDelete("/api/telemetry", ([FromQuery] string? guildId) =>
        {
            lock (LockObj)
            {
                if (string.IsNullOrWhiteSpace(guildId))
                    Units.Clear();
                else
                    Units.Remove(guildId.Trim());

                SaveToDiskUnsafe();
            }

            return Results.Ok(new { ok = true });
        });

        return app;
    }

    private static async Task<TelemetryUnit> ReadTelemetryUnitAsync(HttpRequest req)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object && TryGetElement(root, out var dataEl, "data") && dataEl.ValueKind == JsonValueKind.Object)
            root = dataEl;

        var unit = new TelemetryUnit
        {
            GuildId = GetString(root, "guildId", "GuildId", "serverId", "ServerId") ?? "",
            DriverDiscordUserId = GetString(root, "driverDiscordUserId", "DriverDiscordUserId", "discordUserId", "DiscordUserId") ?? "",

            DiscordUserId = GetString(root, "discordUserId", "DiscordUserId"),
            UserId = GetString(root, "userId", "UserId"),

            Driver = GetString(root, "driver", "Driver", "name", "Name"),
            DriverName = GetString(root, "driverName", "DriverName", "displayName", "DisplayName"),
            Truck = GetString(root, "truck", "Truck"),
            TruckName = GetString(root, "truckName", "TruckName"),
            TruckNumber = GetString(root, "truckNumber", "TruckNumber"),

            X = GetDouble(root, "x", "X") ?? 0,
            Y = GetDouble(root, "y", "Y") ?? 0,
            MapX = GetDouble(root, "mapX", "MapX") ?? 0,
            MapY = GetDouble(root, "mapY", "MapY") ?? 0,
            WorldX = GetDouble(root, "worldX", "WorldX") ?? 0,
            WorldZ = GetDouble(root, "worldZ", "WorldZ") ?? 0,
            MarkerX = GetDouble(root, "markerX", "MarkerX") ?? 0,
            MarkerY = GetDouble(root, "markerY", "MarkerY") ?? 0,

            Longitude = GetDouble(root, "longitude", "Longitude", "gpsLongitude", "GpsLongitude"),
            Latitude = GetDouble(root, "latitude", "Latitude", "gpsLatitude", "GpsLatitude"),
            Lng = GetDouble(root, "lng", "Lng"),
            Lat = GetDouble(root, "lat", "Lat"),
            Lon = GetDouble(root, "lon", "Lon"),

            City = GetString(root, "city", "City"),
            State = GetString(root, "state", "State"),
            Status = GetString(root, "status", "Status", "dutyStatus", "DutyStatus"),

            SourceCity = GetString(root, "sourceCity", "SourceCity", "pickupCity", "PickupCity"),
            SourceCompany = GetString(root, "sourceCompany", "SourceCompany", "pickupCompany", "PickupCompany"),
            DestinationCity = GetString(root, "destinationCity", "DestinationCity", "dropCity", "DropCity"),
            DestinationCompany = GetString(root, "destinationCompany", "DestinationCompany", "dropCompany", "DropCompany")
        };

        return unit;
    }

    private static void NormalizeCoordinates(TelemetryUnit unit)
    {
        if (IsValidLngLat(unit.Longitude, unit.Latitude))
        {
            unit.Lng = unit.Longitude;
            unit.Lon = unit.Longitude;
            unit.Lat = unit.Latitude;
            unit.ConversionMode ??= "GPS";
            return;
        }

        if (IsValidLngLat(unit.Lng, unit.Lat))
        {
            unit.Longitude = unit.Lng;
            unit.Latitude = unit.Lat;
            unit.Lon = unit.Lng;
            unit.ConversionMode = "LNG_LAT";
            return;
        }

        if (IsValidLngLat(unit.Lon, unit.Lat))
        {
            unit.Longitude = unit.Lon;
            unit.Latitude = unit.Lat;
            unit.Lng = unit.Lon;
            unit.ConversionMode = "LON_LAT";
            return;
        }

        var gameX = FirstNonZero(unit.MarkerX, unit.WorldX, unit.MapX, unit.X);
        var gameY = FirstNonZero(unit.MarkerY, unit.WorldZ, unit.MapY, unit.Y);

        if (gameX == 0 && gameY == 0)
        {
            unit.ConversionMode = "NO_COORDINATES";
            return;
        }

        var converted = AtsCoordinateConverter.ToLngLat(gameX, gameY);

        unit.Longitude = converted.Longitude;
        unit.Latitude = converted.Latitude;
        unit.Lng = converted.Longitude;
        unit.Lon = converted.Longitude;
        unit.Lat = converted.Latitude;
        unit.ConversionMode = "ATS_XY_TO_LNGLAT";
    }

    private static bool IsValidLngLat(double? lng, double? lat)
    {
        return lng.HasValue &&
               lat.HasValue &&
               double.IsFinite(lng.Value) &&
               double.IsFinite(lat.Value) &&
               Math.Abs(lng.Value) <= 180 &&
               Math.Abs(lat.Value) <= 90;
    }

    private static double FirstNonZero(params double[] values)
    {
        foreach (var v in values)
        {
            if (double.IsFinite(v) && Math.Abs(v) > 0.000001)
                return v;
        }

        return 0;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static bool TryGetElement(JsonElement root, out JsonElement element, params string[] path)
    {
        element = root;

        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return false;

            if (!element.TryGetProperty(part, out element))
                return false;
        }

        return true;
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;

            try
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                }

                if (el.ValueKind == JsonValueKind.Number ||
                    el.ValueKind == JsonValueKind.True ||
                    el.ValueKind == JsonValueKind.False)
                {
                    return el.ToString();
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;

            try
            {
                if (el.ValueKind == JsonValueKind.Number)
                    return el.GetDouble();

                if (el.ValueKind == JsonValueKind.String &&
                    double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
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

    private static string DataDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string TelemetryFile => Path.Combine(DataDir, "live_telemetry.json");

    private static void LoadFromDisk()
    {
        lock (LockObj)
        {
            try
            {
                if (!File.Exists(TelemetryFile))
                    return;

                var json = File.ReadAllText(TelemetryFile);

                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<TelemetryUnit>>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (loaded == null)
                    return;

                Units.Clear();

                foreach (var kvp in loaded)
                {
                    var fresh = kvp.Value
                        .Where(x => (DateTimeOffset.UtcNow - x.UpdatedUtc).TotalMinutes <= 10)
                        .ToList();

                    if (fresh.Count > 0)
                        Units[kvp.Key] = fresh;
                }
            }
            catch
            {
            }
        }
    }

    private static void SaveToDiskUnsafe()
    {
        try
        {
            var json = JsonSerializer.Serialize(Units, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(TelemetryFile, json);
        }
        catch
        {
        }
    }
}

public static class AtsCoordinateConverter
{
    private const double AtsMinX = -124000.0;
    private const double AtsMaxX = 124000.0;

    private const double AtsMinY = -109500.0;
    private const double AtsMaxY = 170500.0;

    private const double LngMin = -125.1;
    private const double LngMax = -66.8;

    private const double LatMin = 24.0;
    private const double LatMax = 49.5;

    public static (double Longitude, double Latitude) ToLngLat(double x, double y)
    {
        var nx = Clamp((x - AtsMinX) / (AtsMaxX - AtsMinX), 0, 1);
        var ny = Clamp((y - AtsMinY) / (AtsMaxY - AtsMinY), 0, 1);

        var lng = LngMin + nx * (LngMax - LngMin);
        var lat = LatMax - ny * (LatMax - LatMin);

        return (lng, lat);
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}

public sealed class TelemetryUnit
{
    public string GuildId { get; set; } = "";
    public string DriverDiscordUserId { get; set; } = "";

    public string? DiscordUserId { get; set; }
    public string? UserId { get; set; }

    public string? Driver { get; set; }
    public string? DriverName { get; set; }
    public string? Truck { get; set; }
    public string? TruckName { get; set; }
    public string? TruckNumber { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double MapX { get; set; }
    public double MapY { get; set; }
    public double WorldX { get; set; }
    public double WorldZ { get; set; }
    public double MarkerX { get; set; }
    public double MarkerY { get; set; }

    public double? Longitude { get; set; }
    public double? Latitude { get; set; }
    public double? Lng { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    public string? City { get; set; }
    public string? State { get; set; }
    public string? Status { get; set; }
    public string? ConversionMode { get; set; }

    public string? SourceCity { get; set; }
    public string? SourceCompany { get; set; }
    public string? DestinationCity { get; set; }
    public string? DestinationCompany { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
