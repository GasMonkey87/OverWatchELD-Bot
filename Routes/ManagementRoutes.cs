using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class ManagementRoutes
{
    public static void Register(WebApplication app, BotServices services)
    {
        // =============================
        // GET ALL DRIVERS (MAIN PAGE)
        // =============================
        app.MapGet("/api/management/drivers", (HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            var roster = services.RosterStore?.List(guildId) ?? new();
            var status = services.DriverStatusStore?.List(guildId) ?? new();

            var drivers = roster.Select(r =>
            {
                var live = status.FirstOrDefault(s =>
                    string.Equals(s.DiscordUserId, r.DiscordUserId, StringComparison.OrdinalIgnoreCase));

                return new
                {
                    driverId = r.DriverId,
                    name = r.Name,
                    discordUserId = r.DiscordUserId,
                    discordUsername = r.DiscordUsername,
                    role = string.IsNullOrWhiteSpace(r.Role) ? "Driver" : r.Role,
                    truckNumber = r.TruckNumber,
                    notes = r.Notes,

                    // live data
                    dutyStatus = live?.DutyStatus ?? "offline",
                    lastSeenUtc = live?.LastSeenUtc,
                    location = live?.Location,
                    loadNumber = live?.LoadNumber,
                    speedMph = live?.SpeedMph ?? 0
                };
            }).ToArray();

            return Results.Ok(new { ok = true, drivers });
        });

        // =============================
        // UPDATE ROLE (PROMOTE / DEMOTE)
        // =============================
        app.MapPost("/api/management/driver/role", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var guildId = root.GetProperty("guildId").GetString();
            var discordUserId = root.GetProperty("discordUserId").GetString();
            var role = root.GetProperty("role").GetString();

            if (string.IsNullOrWhiteSpace(guildId) ||
                string.IsNullOrWhiteSpace(discordUserId) ||
                string.IsNullOrWhiteSpace(role))
            {
                return Results.Json(new { ok = false, error = "MissingFields" }, statusCode: 400);
            }

            var roster = services.RosterStore?.List(guildId);
            var driver = roster?.FirstOrDefault(x =>
                string.Equals(x.DiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase));

            if (driver == null)
                return Results.Json(new { ok = false, error = "DriverNotFound" }, statusCode: 404);

            driver.Role = role;
            services.RosterStore?.Upsert(driver);

            return Results.Ok(new { ok = true });
        });

        // =============================
        // UPDATE NOTES
        // =============================
        app.MapPost("/api/management/driver/note", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var guildId = root.GetProperty("guildId").GetString();
            var discordUserId = root.GetProperty("discordUserId").GetString();
            var notes = root.GetProperty("notes").GetString();

            var roster = services.RosterStore?.List(guildId);
            var driver = roster?.FirstOrDefault(x =>
                string.Equals(x.DiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase));

            if (driver == null)
                return Results.Json(new { ok = false, error = "DriverNotFound" }, statusCode: 404);

            driver.Notes = notes;
            services.RosterStore?.Upsert(driver);

            return Results.Ok(new { ok = true });
        });

        // =============================
        // FLEET MESSAGE (DISCORD BACKED)
        // =============================
        app.MapPost("/api/management/message/fleet", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var guildId = root.GetProperty("guildId").GetString();
            var text = root.GetProperty("text").GetString();

            if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "MissingFields" }, statusCode: 400);

            var roster = services.RosterStore?.List(guildId) ?? new();

            foreach (var driver in roster)
            {
                if (string.IsNullOrWhiteSpace(driver.DiscordUserId))
                    continue;

                services.DispatchStore?.AddMessage(guildId, new
                {
                    driverDiscordUserId = driver.DiscordUserId,
                    driverName = driver.Name,
                    text = text,
                    direction = "to_driver"
                });
            }

            return Results.Ok(new { ok = true, sent = roster.Count });
        });
    }
}
