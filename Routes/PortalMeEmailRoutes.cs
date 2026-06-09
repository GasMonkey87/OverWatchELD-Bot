using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class PortalMeEmailRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/portal/me-email", (HttpContext ctx, WebSessionStore sessionStore, PortalDataStore portalStore, BotServices services, DiscordSocketClient discord) =>
        {
            var session = GetSession(ctx, sessionStore);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var guildId = FirstNonBlank(ctx.Request.Query["guildId"].ToString(), ctx.Request.Query["vtcId"].ToString(), ctx.Request.Cookies["ow_selected_guild"], session.LockedGuildId);
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (!session.IsEmailAccount && !UserIsInGuild(discord, guildId, session.DiscordUserId))
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            var portal = portalStore.GetGuild(guildId);
            var discordGuild = ulong.TryParse(guildId, out var parsedGuildId) ? discord.GetGuild(parsedGuildId) : null;
            var member = ResolveMember(discordGuild, session.DiscordUserId);
            var role = session.IsEmailAccount && string.IsNullOrWhiteSpace(session.DiscordUserId) ? "Driver" : ResolveMemberRole(discordGuild, member);
            var companyName = FirstNonBlank(portal.CompanyName, portal.SiteTitle, discordGuild?.Name, "Registered VTC");

            var liveStatuses = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();
            var perf = services.PerformanceStore?.Load(guildId) ?? new Dictionary<string, DriverPerformance>(StringComparer.OrdinalIgnoreCase);
            var topPerf = services.PerformanceStore?.GetTop(guildId, 25) ?? new List<DriverPerformance>();

            var roster = BuildRoster(discordGuild, portal, liveStatuses, perf);
            var fleet = BuildFleet(portal, liveStatuses);
            var garages = BuildGarages(portal);
            var latest = BuildLatest(companyName, portal, liveStatuses);
            var jobs = BuildJobs(liveStatuses, perf);
            var leaderboard = BuildLeaderboard(discordGuild, roster, topPerf);
            var maintenance = BuildMaintenance(portal);
            var inspections = BuildInspections();
            var awards = BuildAwards(discordGuild, portal, topPerf);
            var stats = BuildStats(discordGuild, portal, roster.Count, fleet.Count, garages.Count, liveStatuses, perf);

            return Results.Json(new
            {
                ok = true,
                guildId,
                vtc = new
                {
                    guildId,
                    name = companyName,
                    description = FirstNonBlank(portal.WelcomeText, portal.CompanyInfo, $"Welcome to {companyName}."),
                    about = FirstNonBlank(portal.CompanyInfo, portal.WelcomeText, "Owners and admins can write a brief company description here."),
                    logoUrl = FirstNonBlank(portal.LogoImageUrl, discordGuild?.IconUrl, ""),
                    bannerUrl = portal.BannerImageUrl,
                    heroImageUrl = portal.HeroImageUrl,
                    discordUrl = portal.JoinDiscordUrl,
                    myRole = role,
                    canEditPortal = role is "Owner" or "Admin" or "Manager"
                },
                stats,
                roles = BuildRoles(discordGuild, portal),
                roster,
                latest,
                latestInfo = latest,
                jobs,
                loads = jobs,
                leaderboard,
                fleet,
                trucks = fleet,
                garages,
                awards,
                achievements = awards,
                maintenance,
                inspections,
                liveDrivers = liveStatuses.Select(x => new
                {
                    x.GuildId,
                    x.DiscordUserId,
                    x.DriverName,
                    x.DutyStatus,
                    truck = x.Truck,
                    x.LoadNumber,
                    x.Location,
                    x.SpeedMph,
                    x.Latitude,
                    x.Longitude,
                    x.Heading,
                    x.LastSeenUtc,
                    isOnline = x.LastSeenUtc >= DateTimeOffset.UtcNow.AddMinutes(-10)
                }).ToList(),
                sync = new
                {
                    source = "OverWatch ELD BotStores",
                    liveDriverCount = liveStatuses.Count,
                    performanceRows = perf.Count,
                    lastUpdatedUtc = DateTimeOffset.UtcNow
                }
            });
        });
    }

    private static WebSessionUser? GetSession(HttpContext ctx, WebSessionStore sessionStore)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessionId = auth[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = ctx.Request.Headers["X-OverWatch-Session"].ToString();
        return !string.IsNullOrWhiteSpace(sessionId) && sessionStore.TryGet(sessionId, out var session) ? session : null;
    }

    private static bool UserIsInGuild(DiscordSocketClient discord, string guildId, string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(guildId, out var parsedGuildId)) return false;
        return ResolveMember(discord.GetGuild(parsedGuildId), discordUserId) != null;
    }

    private static SocketGuildUser? ResolveMember(SocketGuild? guild, string discordUserId)
    {
        if (guild == null || string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(discordUserId, out var parsedUserId)) return null;
        return guild.GetUser(parsedUserId);
    }

    private static string ResolveMemberRole(SocketGuild? guild, SocketGuildUser? member)
    {
        if (guild == null || member == null) return "Driver";
        if (guild.OwnerId == member.Id) return "Owner";
        if (member.GuildPermissions.Administrator) return "Admin";
        var roles = member.Roles.Select(r => r.Name.ToLowerInvariant()).ToList();
        if (roles.Any(r => r.Contains("owner"))) return "Owner";
        if (roles.Any(r => r.Contains("admin"))) return "Admin";
        if (roles.Any(r => r.Contains("manager") || r.Contains("management"))) return "Manager";
        if (roles.Any(r => r.Contains("dispatch"))) return "Dispatch";
        if (roles.Any(r => r.Contains("mechanic") || r.Contains("maintenance"))) return "Mechanic";
        return "Driver";
    }

    private static List<object> BuildRoles(SocketGuild? guild, PortalGuildData portal)
    {
        var rows = new List<object>();
        foreach (var roleName in new[] { "Owner", "Admin", "Manager", "Dispatch", "Mechanic" })
        {
            var names = guild == null ? new List<string>() : guild.Users.Where(u => !u.IsBot && ResolveMemberRole(guild, u) == roleName).Select(u => FirstNonBlank(u.DisplayName, u.GlobalName, u.Username)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (names.Count == 0) names = portal.ManagementTeam.Where(x => string.Equals(x.Role, roleName, StringComparison.OrdinalIgnoreCase)).Select(x => FirstNonBlank(x.Name, x.DiscordUsername)).Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).ToList();
            rows.Add(new { role = roleName, userName = names.Count == 0 ? "Not assigned" : string.Join(", ", names) });
        }
        return rows;
    }

    private static List<object> BuildRoster(SocketGuild? guild, PortalGuildData portal, List<DriverStatusStore.DriverStatusEntry> live, Dictionary<string, DriverPerformance> perf)
    {
        var rows = new List<object>();
        if (guild != null)
        {
            foreach (var member in guild.Users.Where(u => !u.IsBot).OrderBy(u => u.DisplayName).Take(1000))
            {
                var uid = member.Id.ToString();
                var liveRow = live.FirstOrDefault(x => string.Equals(x.DiscordUserId, uid, StringComparison.OrdinalIgnoreCase));
                var perfRow = perf.TryGetValue(uid, out var p) ? p : null;
                var truck = portal.Trucks.FirstOrDefault(t => string.Equals(t.DriverDiscordUserId, uid, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Driver, member.DisplayName, StringComparison.OrdinalIgnoreCase));
                rows.Add(new
                {
                    userName = FirstNonBlank(liveRow?.DriverName, member.DisplayName, member.GlobalName, member.Username, "Driver"),
                    role = ResolveMemberRole(guild, member),
                    currentTruck = FirstNonBlank(liveRow?.Truck, truck?.Name, truck?.Model, "Unassigned"),
                    totalMileage = perfRow?.MilesTotal.ToString("0") ?? FirstNonBlank(truck?.Odometer, "0"),
                    monthlyMiles = perfRow?.MilesMonth ?? 0,
                    weeklyMiles = perfRow?.MilesWeek ?? 0,
                    loadsTotal = perfRow?.LoadsTotal ?? 0,
                    driverScore = perfRow?.Score.ToString("0") ?? "N/A",
                    status = FirstNonBlank(liveRow?.DutyStatus, member.Status.ToString()),
                    awards = Array.Empty<string>(),
                    location = FirstNonBlank(liveRow?.Location, truck?.Location, "Unknown"),
                    speedMph = liveRow?.SpeedMph ?? 0,
                    loadNumber = liveRow?.LoadNumber ?? "",
                    joined = member.JoinedAt?.ToString("yyyy-MM-dd") ?? "N/A",
                    discordUserId = uid,
                    avatarUrl = member.GetDisplayAvatarUrl(),
                    lastSeenUtc = liveRow?.LastSeenUtc
                });
            }
        }
        foreach (var driver in portal.Drivers.Where(d => string.IsNullOrWhiteSpace(d.DiscordUserId) || rows.All(r => !r.ToString()!.Contains(d.DiscordUserId, StringComparison.OrdinalIgnoreCase))))
        {
            var liveRow = live.FirstOrDefault(x => string.Equals(x.DiscordUserId, driver.DiscordUserId, StringComparison.OrdinalIgnoreCase) || string.Equals(x.DriverName, driver.Name, StringComparison.OrdinalIgnoreCase));
            var perfRow = !string.IsNullOrWhiteSpace(driver.DiscordUserId) && perf.TryGetValue(driver.DiscordUserId, out var p) ? p : null;
            rows.Add(new { userName = FirstNonBlank(liveRow?.DriverName, driver.Name, driver.DiscordUsername, "Driver"), role = FirstNonBlank(driver.Role, "Driver"), currentTruck = FirstNonBlank(liveRow?.Truck, driver.AssignedTruck, driver.FavoriteTruck, "Unassigned"), totalMileage = perfRow?.MilesTotal.ToString("0") ?? FirstNonBlank(driver.TotalMiles, driver.Mileage, "0"), monthlyMiles = perfRow?.MilesMonth ?? 0, weeklyMiles = perfRow?.MilesWeek ?? 0, loadsTotal = perfRow?.LoadsTotal ?? 0, driverScore = perfRow?.Score.ToString("0") ?? "N/A", status = FirstNonBlank(liveRow?.DutyStatus, driver.Status, "Member"), awards = string.IsNullOrWhiteSpace(driver.Achievement) ? Array.Empty<string>() : new[] { driver.Achievement }, location = FirstNonBlank(liveRow?.Location, "Unknown"), speedMph = liveRow?.SpeedMph ?? 0, loadNumber = liveRow?.LoadNumber ?? "", joined = FirstNonBlank(driver.YearsInVtc, "N/A"), discordUserId = driver.DiscordUserId, avatarUrl = driver.DiscordAvatarUrl, lastSeenUtc = liveRow?.LastSeenUtc });
        }
        return rows;
    }

    private static List<object> BuildFleet(PortalGuildData portal, List<DriverStatusStore.DriverStatusEntry> live)
    {
        var rows = portal.Trucks.Select(t =>
        {
            var liveRow = live.FirstOrDefault(x => string.Equals(x.DiscordUserId, t.DriverDiscordUserId, StringComparison.OrdinalIgnoreCase) || string.Equals(x.DriverName, t.Driver, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Truck, t.Name, StringComparison.OrdinalIgnoreCase));
            return new { id = t.Id, truckNumber = t.TruckNumber, name = FirstNonBlank(liveRow?.Truck, t.Name, t.Model, "Truck"), model = t.Model, driver = FirstNonBlank(liveRow?.DriverName, t.Driver, "Unassigned"), plate = t.Plate, odometer = t.Odometer, location = FirstNonBlank(liveRow?.Location, t.Location, "Unknown"), status = FirstNonBlank(liveRow?.DutyStatus, t.Status, "Available"), condition = FirstNonBlank(t.Condition, "N/A"), fuel = FirstNonBlank(t.Fuel, "N/A"), lastSeenUtc = liveRow?.LastSeenUtc };
        }).Cast<object>().ToList();
        foreach (var liveRow in live.Where(x => rows.All(r => !r.ToString()!.Contains(x.DriverName ?? "", StringComparison.OrdinalIgnoreCase))))
        {
            rows.Add(new { id = "live-" + liveRow.DiscordUserId, truckNumber = "", name = FirstNonBlank(liveRow.Truck, "Live Truck"), model = liveRow.Truck, driver = liveRow.DriverName, plate = "", odometer = "", location = liveRow.Location, status = liveRow.DutyStatus, condition = "Live", fuel = "Live", lastSeenUtc = (DateTimeOffset?)liveRow.LastSeenUtc });
        }
        return rows;
    }

    private static List<object> BuildGarages(PortalGuildData portal) => portal.Garages.Select(g => new { id = g.Id, cityName = FirstNonBlank(g.CityName, g.City, g.CityToken, "Garage"), city = FirstNonBlank(g.City, g.CityName, g.CityToken, "Garage"), state = g.State, country = g.Country, size = g.Size, slots = FirstNonBlank(g.Slots, g.TruckCapacity.ToString()), truckCapacity = g.TruckCapacity, isOwned = g.IsOwned }).Cast<object>().ToList();

    private static List<object> BuildLatest(string companyName, PortalGuildData portal, List<DriverStatusStore.DriverStatusEntry> live)
    {
        var items = portal.LatestInfo.OrderByDescending(x => x.CreatedUtc).Take(6).Select(x => new { title = x.Title, body = x.Body, meta = FirstNonBlank(x.Meta, x.CreatedUtc.ToString("yyyy-MM-dd")) }).Cast<object>().ToList();
        foreach (var row in live.OrderByDescending(x => x.LastSeenUtc).Take(4)) items.Add(new { title = $"{row.DriverName} reporting", body = $"{row.Truck} • {row.DutyStatus} • {row.Location}", meta = "Live ELD" });
        if (items.Count == 0) items.Add(new { title = "VTC Connected", body = $"{companyName} is connected to OverWatch ELD.", meta = "Live VTC" });
        return items;
    }

    private static List<object> BuildJobs(List<DriverStatusStore.DriverStatusEntry> live, Dictionary<string, DriverPerformance> perf)
    {
        var rows = live.Where(x => !string.IsNullOrWhiteSpace(x.LoadNumber)).OrderByDescending(x => x.LastSeenUtc).Select(x => new { title = "Active Load " + x.LoadNumber, loadNumber = x.LoadNumber, driver = x.DriverName, origin = "Live ELD", destination = x.Location, miles = "", status = x.DutyStatus, truck = x.Truck, updatedUtc = x.LastSeenUtc }).Cast<object>().ToList();
        if (rows.Count == 0) rows.AddRange(perf.Values.OrderByDescending(x => x.LoadsMonth).Take(6).Select(x => new { title = "Driver Load Summary", loadNumber = "", driver = x.DiscordUserId, origin = "Month", destination = x.LoadsMonth + " loads", miles = x.MilesMonth.ToString("0"), status = "Performance", truck = "", updatedUtc = x.UpdatedUtc }).Cast<object>());
        return rows;
    }

    private static List<object> BuildLeaderboard(SocketGuild? guild, List<object> roster, List<DriverPerformance> topPerf)
    {
        if (topPerf.Count > 0) return topPerf.Select((p, i) => new { rank = i + 1, discordUserId = p.DiscordUserId, userName = ResolveGuildName(guild, p.DiscordUserId), milesWeek = p.MilesWeek, milesMonth = p.MilesMonth, totalMileage = p.MilesTotal, loadsTotal = p.LoadsTotal, score = p.Score, status = "Performance" }).Cast<object>().ToList();
        return roster.Take(10).ToList();
    }

    private static List<object> BuildMaintenance(PortalGuildData portal)
    {
        var rows = portal.Trucks.Where(t => !string.IsNullOrWhiteSpace(t.Condition) || !string.IsNullOrWhiteSpace(t.Notes)).Select(t => new { title = FirstNonBlank(t.TruckNumber, t.Name, t.Model, "Truck"), status = FirstNonBlank(t.Condition, t.Status, "Needs Review"), body = FirstNonBlank(t.Notes, t.Location, "No notes."), driver = t.Driver, location = t.Location }).Cast<object>().ToList();
        if (rows.Count == 0) rows.Add(new { title = "No open maintenance", status = "Clear", body = "No maintenance tickets are synced for this VTC yet.", driver = "", location = "" });
        return rows;
    }

    private static List<object> BuildInspections() => new List<object> { new { title = "Inspection Sync", status = "Ready", body = "Inspection records will appear here when the ELD posts inspection history to the portal.", passed = true } };

    private static List<object> BuildAwards(SocketGuild? guild, PortalGuildData portal, List<DriverPerformance> topPerf)
    {
        var rows = portal.FeaturedDrivers.Where(x => !string.IsNullOrWhiteSpace(x.Achievement)).Select(x => new { title = x.Achievement, body = x.Name, meta = "Driver Award" }).Cast<object>().ToList();
        if (rows.Count == 0 && topPerf.Count > 0) rows.Add(new { title = "Top Performer", body = ResolveGuildName(guild, topPerf[0].DiscordUserId) + " leads the company performance board.", meta = "Performance" });
        return rows;
    }

    private static object BuildStats(SocketGuild? guild, PortalGuildData portal, int rosterCount, int fleetCount, int garageCount, List<DriverStatusStore.DriverStatusEntry> live, Dictionary<string, DriverPerformance> perf)
    {
        return new { members = rosterCount, discordMembers = guild?.Users.Count(u => !u.IsBot) ?? 0, onlineMembers = live.Count(x => x.LastSeenUtc >= DateTimeOffset.UtcNow.AddMinutes(-10)), liveDrivers = live.Count, fleetTrucks = fleetCount, assignedTrucks = portal.Trucks.Count(t => !string.IsNullOrWhiteSpace(t.Driver) || !string.IsNullOrWhiteSpace(t.DriverDiscordUserId)), garages = garageCount, totalMiles = perf.Values.Sum(x => x.MilesTotal), monthMiles = perf.Values.Sum(x => x.MilesMonth), weekMiles = perf.Values.Sum(x => x.MilesWeek), loadsTotal = perf.Values.Sum(x => x.LoadsTotal), loadsMonth = perf.Values.Sum(x => x.LoadsMonth), averageScore = perf.Count == 0 ? 0 : perf.Values.Average(x => x.Score), updatedUtc = portal.UpdatedUtc };
    }

    private static string ResolveGuildName(SocketGuild? guild, string discordUserId)
    {
        if (guild != null && ulong.TryParse(discordUserId, out var id))
        {
            var u = guild.GetUser(id);
            if (u != null) return FirstNonBlank(u.DisplayName, u.GlobalName, u.Username, discordUserId);
        }
        return discordUserId;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }
}
