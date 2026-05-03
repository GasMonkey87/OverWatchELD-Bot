using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot;

public static partial class Program
{
    /// <summary>
    /// Extra routes that live beside Program.cs without top-level statements.
    /// Keeps the bot's explicit Main entry point and prevents CS8804.
    /// </summary>
    private static void RegisterProgramRoutes(WebApplication app, BotServices services, string dataDir)
    {
        app.MapGet("/api/map/live", (string? guildId) =>
        {
            try
            {
                guildId = (guildId ?? "").Trim();

                if (string.IsNullOrWhiteSpace(guildId))
                {
                    return Results.Json(new
                    {
                        ok = true,
                        source = "none",
                        guildId = "",
                        telemetryRows = 0,
                        telemetryCount = 0,
                        statusRows = 0,
                        statusCount = 0,
                        driverCount = 0,
                        points = Array.Empty<object>(),
                        drivers = Array.Empty<object>(),
                        warning = "MissingGuildId"
                    });
                }

                var now = DateTimeOffset.UtcNow;
                var telemetry = LoadTelemetryUnits(dataDir, guildId)
                    .Where(x => (now - x.UpdatedUtc).TotalMinutes <= 30)
                    .ToList();

                var points = telemetry
                    .Where(x => IsValidLngLat(x.Longitude ?? x.Lng ?? x.Lon, x.Latitude ?? x.Lat))
                    .Select(x =>
                    {
                        var lng = x.Longitude ?? x.Lng ?? x.Lon ?? 0;
                        var lat = x.Latitude ?? x.Lat ?? 0;
                        var driverName = FirstNonBlankRoute(x.DriverName, x.Driver, x.DiscordUserId, x.DriverDiscordUserId, "Driver") ?? "Driver";
                        var truckName = FirstNonBlankRoute(x.TruckName, x.Truck, x.TruckNumber, "Truck") ?? "Truck";
                        var location = string.Join(", ", new[] { x.City, x.State }.Where(v => !string.IsNullOrWhiteSpace(v)));

                        return new
                        {
                            id = x.DriverDiscordUserId,
                            driverName,
                            driver = driverName,
                            truck = truckName,
                            truckName,
                            truckNumber = x.TruckNumber ?? "",
                            lat,
                            lng,
                            latitude = lat,
                            longitude = lng,
                            speedMph = x.SpeedMph,
                            speed = x.SpeedMph,
                            heading = x.Heading,
                            dutyStatus = x.Status ?? "Live",
                            status = x.Status ?? "Live",
                            location,
                            city = x.City ?? "",
                            state = x.State ?? "",
                            loadNumber = x.LoadNumber ?? "",
                            cargo = x.CargoName ?? "",
                            cargoName = x.CargoName ?? "",
                            sourceCity = x.SourceCity ?? "",
                            sourceCompany = x.SourceCompany ?? "",
                            destinationCity = x.DestinationCity ?? "",
                            destinationCompany = x.DestinationCompany ?? "",
                            conversionMode = x.ConversionMode ?? "",
                            updatedUtc = x.UpdatedUtc,
                            lastSeenUtc = x.UpdatedUtc
                        };
                    })
                    .Cast<object>()
                    .ToList();

                return Results.Json(new
                {
                    ok = true,
                    source = points.Count > 0 ? "telemetry" : "none",
                    guildId,
                    telemetryRows = telemetry.Count,
                    telemetryCount = telemetry.Count,
                    statusRows = 0,
                    statusCount = 0,
                    driverCount = points.Count,
                    points,
                    drivers = points
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MAP LIVE ERROR] " + ex);

                return Results.Json(new
                {
                    ok = false,
                    source = "error",
                    guildId = guildId ?? "",
                    telemetryRows = 0,
                    telemetryCount = 0,
                    statusRows = 0,
                    statusCount = 0,
                    driverCount = 0,
                    points = Array.Empty<object>(),
                    drivers = Array.Empty<object>(),
                    error = ex.Message
                });
            }
        });
    }

    private static List<TelemetryUnit> LoadTelemetryUnits(string dataDir, string guildId)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(dataDir, "live_telemetry.json"),
                Path.Combine(AppContext.BaseDirectory, "data", "live_telemetry.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "data", "live_telemetry.json")
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path == null)
                return new List<TelemetryUnit>();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new List<TelemetryUnit>();

            var dict = JsonSerializer.Deserialize<Dictionary<string, List<TelemetryUnit>>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (dict == null)
                return new List<TelemetryUnit>();

            return dict.TryGetValue(guildId, out var units)
                ? units ?? new List<TelemetryUnit>()
                : new List<TelemetryUnit>();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[MAP TELEMETRY LOAD ERROR] " + ex.Message);
            return new List<TelemetryUnit>();
        }
    }

    private static bool IsValidLngLat(double? lng, double? lat)
    {
        return lng.HasValue &&
               lat.HasValue &&
               double.IsFinite(lng.Value) &&
               double.IsFinite(lat.Value) &&
               Math.Abs(lng.Value) <= 180 &&
               Math.Abs(lat.Value) <= 90 &&
               !(Math.Abs(lng.Value) < 0.000001 && Math.Abs(lat.Value) < 0.000001);
    }

    private static string? FirstNonBlankRoute(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
