using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class PortalMeEmailRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/portal/me-email", (HttpContext ctx, WebSessionStore sessionStore, PortalDataStore portalStore, DiscordSocketClient discord) =>
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
            var roster = BuildRoster(discordGuild, portal);
            var fleet = portal.Trucks.Select(t => new { id = t.Id, truckNumber = t.TruckNumber, name = FirstNonBlank(t.Name, t.Model, "Truck"), model = t.Model, driver = t.Driver, plate = t.Plate, odometer = t.Odometer, location = t.Location, status = t.Status }).Cast<object>().ToList();
            var garages = portal.Garages.Select(g => new { id = g.Id, cityName = FirstNonBlank(g.CityName, g.City, g.CityToken, "Garage"), city = FirstNonBlank(g.City, g.CityName, g.CityToken, "Garage"), state = g.State, country = g.Country, size = g.Size, slots = FirstNonBlank(g.Slots, g.TruckCapacity.ToString()), truckCapacity = g.TruckCapacity, isOwned = g.IsOwned }).Cast<object>().ToList();
            var latest = portal.LatestInfo.OrderByDescending(x => x.CreatedUtc).Take(10).Select(x => new { title = x.Title, body = x.Body, meta = FirstNonBlank(x.Meta, x.CreatedUtc.ToString("yyyy-MM-dd")) }).Cast<object>().ToList();
            if (latest.Count == 0) latest.Add(new { title = "VTC Connected", body = $"{companyName} is connected to OverWatch ELD.", meta = "Live VTC" });

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
                stats = new
                {
                    members = roster.Count,
                    discordMembers = discordGuild?.Users.Count(u => !u.IsBot) ?? 0,
                    onlineMembers = discordGuild?.Users.Count(u => !u.IsBot && u.Status is UserStatus.Online or UserStatus.Idle or UserStatus.DoNotDisturb) ?? 0,
                    fleetTrucks = fleet.Count,
                    assignedTrucks = portal.Trucks.Count(t => !string.IsNullOrWhiteSpace(t.Driver) || !string.IsNullOrWhiteSpace(t.DriverDiscordUserId)),
                    garages = garages.Count,
                    updatedUtc = portal.UpdatedUtc
                },
                roles = BuildRoles(discordGuild, portal),
                roster,
                latest,
                latestInfo = latest,
                fleet,
                trucks = fleet,
                garages
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

    private static List<object> BuildRoster(SocketGuild? guild, PortalGuildData portal)
    {
        var rows = new List<object>();
        if (guild != null)
        {
            foreach (var member in guild.Users.Where(u => !u.IsBot).OrderBy(u => u.DisplayName).Take(1000))
            {
                var truck = portal.Trucks.FirstOrDefault(t => string.Equals(t.DriverDiscordUserId, member.Id.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(t.Driver, member.DisplayName, StringComparison.OrdinalIgnoreCase));
                rows.Add(new { userName = FirstNonBlank(member.DisplayName, member.GlobalName, member.Username, "Driver"), role = ResolveMemberRole(guild, member), currentTruck = FirstNonBlank(truck?.Name, truck?.Model, "Unassigned"), totalMileage = FirstNonBlank(truck?.Odometer, "0"), status = member.Status.ToString(), awards = Array.Empty<string>(), location = FirstNonBlank(truck?.Location, "Unknown"), driverScore = "N/A", joined = member.JoinedAt?.ToString("yyyy-MM-dd") ?? "N/A", discordUserId = member.Id.ToString(), avatarUrl = member.GetDisplayAvatarUrl() });
            }
        }
        foreach (var driver in portal.Drivers.Where(d => string.IsNullOrWhiteSpace(d.DiscordUserId) || rows.All(r => !r.ToString()!.Contains(d.DiscordUserId, StringComparison.OrdinalIgnoreCase))))
        {
            rows.Add(new { userName = FirstNonBlank(driver.Name, driver.DiscordUsername, "Driver"), role = FirstNonBlank(driver.Role, "Driver"), currentTruck = FirstNonBlank(driver.AssignedTruck, driver.FavoriteTruck, "Unassigned"), totalMileage = FirstNonBlank(driver.TotalMiles, driver.Mileage, "0"), status = FirstNonBlank(driver.Status, "Member"), awards = string.IsNullOrWhiteSpace(driver.Achievement) ? Array.Empty<string>() : new[] { driver.Achievement }, location = "Unknown", driverScore = "N/A", joined = FirstNonBlank(driver.YearsInVtc, "N/A"), discordUserId = driver.DiscordUserId, avatarUrl = driver.DiscordAvatarUrl });
        }
        return rows;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }
}
