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
                Units.TryGetValue(guildId, out var units);
                return Results.Ok(new
                {
                    ok = true,
                    data = units ?? new List<TelemetryUnit>()
                });
            }
        });

        app.MapPost("/api/telemetry", ([FromBody] TelemetryUnit unit) =>
        {
            if (string.IsNullOrWhiteSpace(unit.GuildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            if (string.IsNullOrWhiteSpace(unit.DriverDiscordUserId))
                unit.DriverDiscordUserId = unit.Driver ?? unit.Truck ?? Guid.NewGuid().ToString("N");

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

        return app;
    }
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

    public string? City { get; set; }
    public string? State { get; set; }
    public string? Status { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
