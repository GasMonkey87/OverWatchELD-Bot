using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class DashboardRoutes
{
    public static void Register(IEndpointRouteBuilder app, BotServices services, JsonSerializerOptions jsonWrite)
    {
        app.MapGet("/dashboard", () => Results.Redirect("/index.html"));
        app.MapGet("/live-map", () => Results.Redirect("/live-map.html"));

        app.MapGet("/api/dashboard/summary", (HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var guild = ResolveGuild(services, guildId);
            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var linked = services.LinkedDriversStore?.List(guildId) ?? new List<LinkedDriverEntry>();
            var top = services.PerformanceStore?.GetTop(guildId, 5) ?? new List<DriverPerformance>();
            var settings = services.DispatchStore?.Get(guildId);

            var onlineCount = guild?.Users.Count(u => !u.IsBot && IsOnline(u.Status)) ?? 0;

            var topDrivers = top.Select(p => new
            {
                discordUserId = p.DiscordUserId,
                name = ResolveDriverName(guild, roster, linked, p.DiscordUserId),
                score = p.Score,
                milesWeek = p.MilesWeek,
                loadsWeek = p.LoadsWeek,
                performancePct = p.PerformancePct
            }).ToArray();

            return Results.Json(new
            {
                ok = true,
                guildId,
                vtcName = guild?.Name ?? "Unknown VTC",
                driversTotal = roster.Count,
                driversOnline = onlineCount,
                pairedDrivers = linked.Count,
                dispatchReady = !string.IsNullOrWhiteSpace(settings?.DispatchChannelId),
                announcementsReady = !string.IsNullOrWhiteSpace(settings?.AnnouncementChannelId),
                topDrivers
            }, jsonWrite);
        });

        app.MapGet("/api/dashboard/drivers", async (HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var guild = ResolveGuild(services, guildId);
            if (guild != null)
            {
                try { await guild.DownloadUsersAsync(); } catch { }
            }

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var linked = services.LinkedDriversStore?.List(guildId) ?? new List<LinkedDriverEntry>();
            var perfById = (services.PerformanceStore?.Load(guildId) ?? new Dictionary<string, DriverPerformance>(StringComparer.OrdinalIgnoreCase))
                .ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

            var rows = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var driver in roster.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var userId = (driver.DiscordUserId ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(userId))
                    seen.Add(userId);

                perfById.TryGetValue(userId, out var perf);
                var linkedEntry = !string.IsNullOrWhiteSpace(userId)
                    ? linked.FirstOrDefault(x => string.Equals(x.DiscordUserId, userId, StringComparison.OrdinalIgnoreCase))
                    : null;
                var guildUser = ResolveGuildUser(guild, userId);

                rows.Add(new
                {
                    name = string.IsNullOrWhiteSpace(driver.Name) ? (guildUser?.DisplayName ?? guildUser?.Username ?? "Unknown Driver") : driver.Name,
                    role = string.IsNullOrWhiteSpace(driver.Role) ? "driver" : driver.Role,
                    status = NormalizeStatus(driver.Status, guildUser?.Status),
                    paired = linkedEntry != null,
                    truck = driver.TruckNumber ?? "",
                    score = perf?.Score ?? 0,
                    weekMiles = perf?.MilesWeek ?? 0,
                    loads = perf?.LoadsWeek ?? 0,
                    discordUserId = userId
                });
            }

            foreach (var extra in linked.Where(x => !seen.Contains(x.DiscordUserId)))
            {
                perfById.TryGetValue(extra.DiscordUserId, out var perf);
                var guildUser = ResolveGuildUser(guild, extra.DiscordUserId);
                rows.Add(new
                {
                    name = guildUser?.DisplayName ?? guildUser?.Username ?? extra.DiscordUserName ?? extra.DiscordUserId,
                    role = "driver",
                    status = NormalizeStatus(null, guildUser?.Status),
                    paired = true,
                    truck = "",
                    score = perf?.Score ?? 0,
                    weekMiles = perf?.MilesWeek ?? 0,
                    loads = perf?.LoadsWeek ?? 0,
                    discordUserId = extra.DiscordUserId
                });
            }

            return Results.Json(new { ok = true, guildId, drivers = rows }, jsonWrite);
        });

        app.MapGet("/api/dashboard/performance", (HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var guild = ResolveGuild(services, guildId);
            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var linked = services.LinkedDriversStore?.List(guildId) ?? new List<LinkedDriverEntry>();

            var take = 10;
            if (int.TryParse(req.Query["take"].ToString(), out var parsed) && parsed > 0)
                take = Math.Min(parsed, 50);

            var rows = (services.PerformanceStore?.GetTop(guildId, take) ?? new List<DriverPerformance>())
                .Select(x => new
                {
                    discordUserId = x.DiscordUserId,
                    driverName = ResolveDriverName(guild, roster, linked, x.DiscordUserId),
                    milesWeek = x.MilesWeek,
                    milesMonth = x.MilesMonth,
                    milesTotal = x.MilesTotal,
                    loadsWeek = x.LoadsWeek,
                    loadsMonth = x.LoadsMonth,
                    loadsTotal = x.LoadsTotal,
                    performancePct = x.PerformancePct,
                    score = x.Score,
                    updatedUtc = x.UpdatedUtc
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId, rows }, jsonWrite);
        });

        app.MapGet("/api/dashboard/settings", (HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var settings = services.DispatchStore?.Get(guildId) ?? new DispatchSettings { GuildId = guildId };

            return Results.Json(new
            {
                ok = true,
                settings = new
                {
                    guildId,
                    dispatchChannelId = settings.DispatchChannelId ?? "",
                    dispatchWebhookUrl = settings.DispatchWebhookUrl ?? "",
                    announcementChannelId = settings.AnnouncementChannelId ?? "",
                    announcementWebhookUrl = settings.AnnouncementWebhookUrl ?? ""
                }
            }, jsonWrite);
        });

        app.MapPut("/api/dashboard/settings", async (HttpRequest req) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                var root = doc.RootElement;
                var guildId = FirstString(root, "guildId", "GuildId");
                if (string.IsNullOrWhiteSpace(guildId))
                    guildId = ResolveGuildId(req, services);

                var dispatchChannelId = FirstString(root, "dispatchChannelId", "DispatchChannelId");
                var dispatchWebhookUrl = FirstString(root, "dispatchWebhookUrl", "DispatchWebhookUrl");
                var announcementChannelId = FirstString(root, "announcementChannelId", "AnnouncementChannelId");
                var announcementWebhookUrl = FirstString(root, "announcementWebhookUrl", "AnnouncementWebhookUrl");

                if (services.DispatchStore == null)
                    return Results.Json(new { ok = false, error = "DispatchStoreNotReady" }, statusCode: 503);

                if (ulong.TryParse(dispatchChannelId, out var dispatchCh) && dispatchCh > 0)
                    services.DispatchStore.SetDispatchChannel(guildId, dispatchCh);

                services.DispatchStore.SetDispatchWebhook(guildId, dispatchWebhookUrl);

                if (ulong.TryParse(announcementChannelId, out var announcementCh) && announcementCh > 0)
                    services.DispatchStore.SetAnnouncementChannel(guildId, announcementCh);

                services.DispatchStore.SetAnnouncementWebhook(guildId, announcementWebhookUrl);

                var settings = services.DispatchStore.Get(guildId);
                return Results.Json(new
                {
                    ok = true,
                    settings = new
                    {
                        guildId,
                        dispatchChannelId = settings.DispatchChannelId ?? "",
                        dispatchWebhookUrl = settings.DispatchWebhookUrl ?? "",
                        announcementChannelId = settings.AnnouncementChannelId ?? "",
                        announcementWebhookUrl = settings.AnnouncementWebhookUrl ?? ""
                    }
                }, jsonWrite);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });
    }

    private static string ResolveGuildId(HttpRequest req, BotServices services)
    {
        var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(guildId))
            return guildId;

        return services.Client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "0";
    }

    private static SocketGuild? ResolveGuild(BotServices services, string guildId)
        => services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);

    private static SocketGuildUser? ResolveGuildUser(SocketGuild? guild, string? discordUserId)
    {
        if (guild == null || !ulong.TryParse((discordUserId ?? "").Trim(), out var id) || id == 0)
            return null;

        return guild.GetUser(id);
    }

    private static bool IsOnline(UserStatus status)
        => status == UserStatus.Online || status == UserStatus.Idle || status == UserStatus.DoNotDisturb;

    private static string NormalizeStatus(string? storedStatus, UserStatus? discordStatus)
    {
        if (!string.IsNullOrWhiteSpace(storedStatus))
            return storedStatus.Trim();

        return discordStatus?.ToString().ToLowerInvariant() ?? "offline";
    }

    private static string ResolveDriverName(SocketGuild? guild, List<VtcDriver> roster, List<LinkedDriverEntry> linked, string? discordUserId)
    {
        var uid = (discordUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return "Unknown Driver";

        var rosterHit = roster.FirstOrDefault(x => string.Equals((x.DiscordUserId ?? "").Trim(), uid, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(rosterHit?.Name))
            return rosterHit!.Name;

        var guildUser = ResolveGuildUser(guild, uid);
        if (!string.IsNullOrWhiteSpace(guildUser?.DisplayName))
            return guildUser.DisplayName;
        if (!string.IsNullOrWhiteSpace(guildUser?.Username))
            return guildUser.Username;

        var linkedHit = linked.FirstOrDefault(x => string.Equals(x.DiscordUserId, uid, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(linkedHit?.DiscordUserName))
            return linkedHit!.DiscordUserName;

        return uid;
    }

    private static string FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v))
            {
                var s = v.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return "";
    }
}
