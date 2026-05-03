using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseStaticFiles();

var TelemetryStore = new List<TelemetryDto>();

// =============================
// 📡 TELEMETRY POST (FROM ELD)
// =============================
app.MapPost("/api/telemetry", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();

    try
    {
        var data = JsonSerializer.Deserialize<TelemetryDto>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (data != null)
        {
            data.Timestamp = DateTime.UtcNow;

            // Remove old entries for same driver
            TelemetryStore.RemoveAll(x =>
                x.GuildId == data.GuildId &&
                x.DriverName == data.DriverName);

            TelemetryStore.Add(data);

            Console.WriteLine($"[TELEMETRY] {data.DriverName} X:{data.WorldX} Z:{data.WorldZ}");
        }

        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// =============================
// 📡 TELEMETRY GET (DEBUG)
// =============================
app.MapGet("/api/telemetry", (string guildId) =>
{
    var data = TelemetryStore
        .Where(t => t.GuildId == guildId)
        .ToList();

    return Results.Json(data);
});

// =============================
// 🗺️ ATS → LAT/LNG CONVERSION
// =============================
static (double lat, double lng)? ConvertAtsWorldToLatLng(double worldX, double worldZ)
{
    if (Math.Abs(worldX) < 1 && Math.Abs(worldZ) < 1)
        return null;

    // USA center
    const double centerLat = 39.5;
    const double centerLng = -98.35;

    const double scale = 18000.0;

    double lat = centerLat - (worldZ / scale);
    double lng = centerLng + (worldX / scale);

    if (lat < 10 || lat > 70 || lng < -170 || lng > -50)
        return null;

    return (lat, lng);
}

// =============================
// 🗺️ LIVE MAP DATA
// =============================
app.MapGet("/api/map/live", (string guildId) =>
{
    var now = DateTime.UtcNow;

    var telemetry = TelemetryStore
        .Where(t => t.GuildId == guildId && (now - t.Timestamp).TotalMinutes < 30)
        .ToList();

    var drivers = new List<object>();

    foreach (var t in telemetry)
    {
        var converted = ConvertAtsWorldToLatLng(t.WorldX, t.WorldZ);

        if (converted == null)
            continue;

        drivers.Add(new
        {
            driver = t.DriverName,
            truck = t.TruckName,
            lat = converted.Value.lat,
            lng = converted.Value.lng,
            speed = t.Speed,
            status = t.Status ?? "Driving"
        });
    }

    return Results.Json(new
    {
        ok = true,
        source = "telemetry",
        telemetryRows = telemetry.Count,
        driverCount = drivers.Count,
        drivers = drivers
    });
});

// =============================
// 🚀 RUN
// =============================
app.Run();

// =============================
// 📦 MODEL
// =============================
public class TelemetryDto
{
    public string GuildId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string TruckName { get; set; } = "";
    public double WorldX { get; set; }
    public double WorldZ { get; set; }
    public double Speed { get; set; }
    public string? Status { get; set; }
    public DateTime Timestamp { get; set; }
}
