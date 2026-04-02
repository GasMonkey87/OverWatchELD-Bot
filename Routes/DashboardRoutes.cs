using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class DashboardRoutes
{
    public static void Register(WebApplication app, BotServices services, System.Text.Json.JsonSerializerOptions jsonOpts)
    {
        // -----------------------------
        // SUMMARY
        // -----------------------------
        app.MapGet("/api/dashboard/summary", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                guildId = services.Client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

            var guild = services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);

            var perf = services.PerformanceStore?.GetTop(guildId, 5) ?? new();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                vtcName = guild?.Name ?? "Unknown",
                driversTotal = guild?.Users.Count ?? 0,
                driversOnline = guild?.Users.Count(u =>
                    u.Status == Discord.UserStatus.Online ||
                    u.Status == Discord.UserStatus.Idle ||
                    u.Status == Discord.UserStatus.DoNotDisturb) ?? 0,
                pairedDrivers = services.LinkedDriversStore?.GetAll()
                    ?.Count(x => x.GuildId == guildId) ?? 0,
                dispatchReady = true,
                announcementsReady = true,
                topDrivers = perf.Select(p => new
                {
                    name = p.DriverName ?? p.DriverId,
                    score = p.Score,
                    milesWeek = p.MilesWeek,
                    loadsWeek = p.LoadsWeek
                })
            });
        });

        // -----------------------------
        // DRIVERS
        // -----------------------------
        app.MapGet("/api/dashboard/drivers", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                guildId = services.Client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

            var guild = services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);
            if (guild == null)
                return Results.Ok(new { ok = true, drivers = Array.Empty<object>() });

            var linked = services.LinkedDriversStore?.GetAll()
                ?.Where(x => x.GuildId == guildId)
                .ToList() ?? new();

            var perf = services.PerformanceStore?.GetTop(guildId, 500) ?? new();

            var drivers = guild.Users.Select(user =>
            {
                var id = user.Id.ToString();

                var link = linked.FirstOrDefault(x => x.DiscordUserId == id);
                var p = perf.FirstOrDefault(x => x.DriverId == id);

                return new
                {
                    name = user.DisplayName,
                    role = "driver",
                    status = user.Status.ToString().ToLowerInvariant(),
                    paired = link != null,
                    truck = "",
                    score = p?.Score ?? 0,
                    weekMiles = p?.MilesWeek ?? 0,
                    loads = p?.LoadsWeek ?? 0
                };
            });

            return Results.Ok(new
            {
                ok = true,
                guildId,
                drivers
            });
        });

        // -----------------------------
        // PERFORMANCE
        // -----------------------------
        app.MapGet("/api/dashboard/performance", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                guildId = services.Client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

            var take = 10;
            int.TryParse(req.Query["take"], out take);

            var perf = services.PerformanceStore?.GetTop(guildId, take) ?? new();

            return Results.Ok(new
            {
                ok = true,
                rows = perf
            });
        });

        // -----------------------------
        // SETTINGS
        // -----------------------------
        app.MapGet("/api/dashboard/settings", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString().Trim();

            var s = services.DispatchStore?.Get(guildId);

            return Results.Ok(new
            {
                ok = true,
                settings = s ?? new { }
            });
        });

        app.MapPut("/api/dashboard/settings", async (HttpRequest req) =>
        {
            try
            {
                var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
                var root = body.RootElement;

                var guildId = root.GetProperty("guildId").GetString() ?? "";
                var channelId = root.GetProperty("dispatchChannelId").GetString() ?? "";

                if (ulong.TryParse(channelId, out var ch))
                {
                    services.DispatchStore?.SetDispatchChannel(guildId, ch);
                }

                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });
    }
}
