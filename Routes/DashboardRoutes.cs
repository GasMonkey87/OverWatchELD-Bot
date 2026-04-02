using System;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class DashboardRoutes
{
    public static void Register(WebApplication app, BotServices services, System.Text.Json.JsonSerializerOptions jsonOpts)
    {
        app.MapGet("/api/dashboard/summary", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!AuthGuard.IsLoggedIn(ctx))
                return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

            if (!AuthGuard.CanManageGuild(ctx, guildId))
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            var guild = services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);
            if (guild == null)
                return Results.Json(new { ok = false, error = "BotNotInGuild", guildId }, statusCode: 404);

            var linkedIds = services.LinkedDriversStore != null
                ? services.LinkedDriversStore
                    .List(guildId)
                    .Select(x => x.DiscordUserId ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var topDrivers = new List<object>();
            if (services.PerformanceStore != null)
            {
                foreach (var p in services.PerformanceStore.GetTop(guildId, 5))
                {
                    var user = guild.Users.FirstOrDefault(u => u.Id.ToString() == p.DiscordUserId);
                    topDrivers.Add(new
                    {
                        discordUserId = p.DiscordUserId,
                        name = user?.DisplayName ?? p.DiscordUserId,
                        score = p.Score,
                        milesWeek = p.MilesWeek,
                        loadsWeek = p.LoadsWeek,
                        performancePct = p.PerformancePct
                    });
                }
            }

            var settings = services.DispatchStore?.Get(guildId);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                vtcName = guild.Name,
                driversTotal = guild.Users.Count,
                driversOnline = guild.Users.Count(u =>
                    u.Status == UserStatus.Online ||
                    u.Status == UserStatus.Idle ||
                    u.Status == UserStatus.DoNotDisturb),
                pairedDrivers = linkedIds.Count,
                dispatchReady = !string.IsNullOrWhiteSpace(settings?.DispatchChannelId),
                announcementsReady = !string.IsNullOrWhiteSpace(settings?.AnnouncementChannelId),
                topDrivers
            });
        });

        app.MapGet("/api/dashboard/drivers", (HttpContext ctx, HttpRequest req) =>
        {
            try
            {
                var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(guildId))
                    return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

                if (!AuthGuard.IsLoggedIn(ctx))
                    return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

                if (!AuthGuard.CanManageGuild(ctx, guildId))
                    return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

                var guild = services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);
                if (guild == null)
                    return Results.Json(new { ok = false, error = "BotNotInGuild", guildId }, statusCode: 404);

                var linkedIds = services.LinkedDriversStore != null
                    ? services.LinkedDriversStore
                        .List(guildId)
                        .Select(x => x.DiscordUserId ?? "")
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var perfMap = new Dictionary<string, (double Score, double MilesWeek, int LoadsWeek)>(StringComparer.OrdinalIgnoreCase);
                if (services.PerformanceStore != null)
                {
                    foreach (var p in services.PerformanceStore.GetTop(guildId, 500))
                    {
                        var key = p.DiscordUserId ?? "";
                        if (!string.IsNullOrWhiteSpace(key))
                            perfMap[key] = (p.Score, p.MilesWeek, p.LoadsWeek);
                    }
                }

                var drivers = guild.Users
                    .OrderBy(u => u.DisplayName)
                    .Select(user =>
                    {
                        var discordUserId = user.Id.ToString();
                        perfMap.TryGetValue(discordUserId, out var perf);

                        return new
                        {
                            discordUserId,
                            name = user.DisplayName,
                            role = "driver",
                            status = user.Status.ToString().ToLowerInvariant(),
                            paired = linkedIds.Contains(discordUserId),
                            truck = "",
                            loadNumber = "",
                            location = "",
                            score = perf.Score,
                            weekMiles = perf.MilesWeek,
                            loads = perf.LoadsWeek
                        };
                    })
                    .ToList();

                return Results.Ok(new
                {
                    ok = true,
                    guildId,
                    drivers
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

        app.MapGet("/api/dashboard/performance", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!AuthGuard.IsLoggedIn(ctx))
                return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

            if (!AuthGuard.CanManageGuild(ctx, guildId))
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            var take = 10;
            if (int.TryParse(req.Query["take"], out var parsed) && parsed > 0)
                take = parsed;

            var guild = services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);
            if (guild == null)
                return Results.Json(new { ok = false, error = "BotNotInGuild", guildId }, statusCode: 404);

            var rows = new List<object>();
            if (services.PerformanceStore != null)
            {
                foreach (var p in services.PerformanceStore.GetTop(guildId, take))
                {
                    var user = guild.Users.FirstOrDefault(u => u.Id.ToString() == p.DiscordUserId);
                    rows.Add(new
                    {
                        discordUserId = p.DiscordUserId,
                        driverName = user?.DisplayName ?? p.DiscordUserId,
                        milesWeek = p.MilesWeek,
                        milesMonth = p.MilesMonth,
                        milesTotal = p.MilesTotal,
                        loadsWeek = p.LoadsWeek,
                        loadsMonth = p.LoadsMonth,
                        loadsTotal = p.LoadsTotal,
                        performancePct = p.PerformancePct,
                        score = p.Score,
                        updatedUtc = p.UpdatedUtc
                    });
                }
            }

            return Results.Ok(new
            {
                ok = true,
                guildId,
                rows
            });
        });

        app.MapGet("/api/dashboard/settings", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!AuthGuard.IsLoggedIn(ctx))
                return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

            if (!AuthGuard.CanManageGuild(ctx, guildId))
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            var settings = services.DispatchStore?.Get(guildId);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                settings
            });
        });

        app.MapPut("/api/dashboard/settings", async (HttpContext ctx, HttpRequest req) =>
        {
            try
            {
                using var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
                var root = body.RootElement;

                var guildId = root.TryGetProperty("guildId", out var guildProp)
                    ? (guildProp.GetString() ?? "").Trim()
                    : "";

                if (string.IsNullOrWhiteSpace(guildId))
                    return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

                if (!AuthGuard.IsLoggedIn(ctx))
                    return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

                if (!AuthGuard.CanManageGuild(ctx, guildId))
                    return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

                var dispatchChannelId = root.TryGetProperty("dispatchChannelId", out var dispatchChannelProp)
                    ? (dispatchChannelProp.GetString() ?? "").Trim()
                    : "";

                var dispatchWebhookUrl = root.TryGetProperty("dispatchWebhookUrl", out var dispatchWebhookProp)
                    ? (dispatchWebhookProp.GetString() ?? "").Trim()
                    : "";

                var announcementChannelId = root.TryGetProperty("announcementChannelId", out var announcementChannelProp)
                    ? (announcementChannelProp.GetString() ?? "").Trim()
                    : "";

                var announcementWebhookUrl = root.TryGetProperty("announcementWebhookUrl", out var announcementWebhookProp)
                    ? (announcementWebhookProp.GetString() ?? "").Trim()
                    : "";

                if (ulong.TryParse(dispatchChannelId, out var dispatchChannel) && dispatchChannel > 0)
                    services.DispatchStore?.SetDispatchChannel(guildId, dispatchChannel);

                if (!string.IsNullOrWhiteSpace(dispatchWebhookUrl))
                    services.DispatchStore?.SetDispatchWebhook(guildId, dispatchWebhookUrl);

                if (ulong.TryParse(announcementChannelId, out var announcementChannel) && announcementChannel > 0)
                    services.DispatchStore?.SetAnnouncementChannel(guildId, announcementChannel);

                if (!string.IsNullOrWhiteSpace(announcementWebhookUrl))
                    services.DispatchStore?.SetAnnouncementWebhook(guildId, announcementWebhookUrl);

                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });
    }
}
