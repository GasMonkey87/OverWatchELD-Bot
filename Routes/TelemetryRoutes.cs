using Microsoft.AspNetCore.Mvc;

namespace OverWatchELD.VtcBot.Routes;

public static class TelemetryRoutes
{
    private static readonly Dictionary<string, List<TelemetryUnit>> Units = new();
    private static readonly object LockObj = new();

    public static WebApplication MapTelemetryRoutes(this WebApplication app)
    {
        app.MapGet("/api/telemetry", ([FromQuery] string guildId) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            lock (LockObj)
            {
                if (!Units.TryGetValue(guildId, out var units))
                    units = new List<TelemetryUnit>();

                units.RemoveAll(x => (DateTimeOffset.UtcNow - x.UpdatedUtc).TotalSeconds > 30);

                return Results.Ok(new { ok = true, data = units });
            }
        });

        app.MapPost("/api/telemetry", ([FromBody] TelemetryUnit unit) =>
        {
            if (string.IsNullOrWhiteSpace(unit.GuildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            if (string.IsNullOrWhiteSpace(unit.DriverDiscordUserId))
            {
                unit.DriverDiscordUserId =
                    unit.Driver ??
                    unit.DriverName ??
                    unit.Truck ??
                    Guid.NewGuid().ToString("N");
            }

            NormalizeCoordinates(unit);
            unit.UpdatedUtc = DateTimeOffset.UtcNow;

            lock (LockObj)
            {
                if (!Units.TryGetValue(unit.GuildId, out var list))
                {
                    list = new List<TelemetryUnit>();
                    Units[unit.GuildId] = list;
                }

                list.RemoveAll(x => x.DriverDiscordUserId == unit.DriverDiscordUserId);
                list.Add(unit);
            }

            return Results.Ok(new { ok = true, data = unit });
        });

        app.MapDelete("/api/telemetry", ([FromQuery] string guildId) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            lock (LockObj)
            {
                Units.Remove(guildId);
            }

            return Results.Ok(new { ok = true });
        });

        return app;
    }

    private static void NormalizeCoordinates(TelemetryUnit unit)
    {
        if (unit.Longitude.HasValue && unit.Latitude.HasValue)
            return;

        if (unit.Lng.HasValue && unit.Lat.HasValue)
        {
            unit.Longitude = unit.Lng;
            unit.Latitude = unit.Lat;
            return;
        }

        if (unit.Lon.HasValue && unit.Lat.HasValue)
        {
            unit.Longitude = unit.Lon;
            unit.Latitude = unit.Lat;
            return;
        }

        var gameX = unit.MapX != 0 ? unit.MapX : unit.X;
        var gameY = unit.MapY != 0 ? unit.MapY : unit.Y;

        var converted = AtsCoordinateConverter.ToLngLat(gameX, gameY);

        unit.Longitude = converted.Longitude;
        unit.Latitude = converted.Latitude;
        unit.Lng = converted.Longitude;
        unit.Lat = converted.Latitude;
        unit.ConversionMode = "ATS_XY_TO_LNGLAT";
    }
}

public static class AtsCoordinateConverter
{
    private const double AtsMinX = -124000.0;
    private const double AtsMaxX =  124000.0;

    private const double AtsMinY = -109500.0;
    private const double AtsMaxY =  170500.0;

    private const double LngMin = -125.1;
    private const double LngMax =  -66.8;

    private const double LatMin =   24.0;
    private const double LatMax =   49.5;

    public static (double Longitude, double Latitude) ToLngLat(double x, double y)
    {
        var nx = Clamp((x - AtsMinX) / (AtsMaxX - AtsMinX), 0, 1);
        var ny = Clamp((y - AtsMinY) / (AtsMaxY - AtsMinY), 0, 1);

        var lng = LngMin + (nx * (LngMax - LngMin));
        var lat = LatMax - (ny * (LatMax - LatMin));

        return (lng, lat);
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}

public sealed class TelemetryUnit
{
    public string GuildId { get; set; } = "";
    public string DriverDiscordUserId { get; set; } = "";

    public string? Driver { get; set; }
    public string? DriverName { get; set; }
    public string? Truck { get; set; }
    public string? TruckNumber { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double MapX { get; set; }
    public double MapY { get; set; }

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
