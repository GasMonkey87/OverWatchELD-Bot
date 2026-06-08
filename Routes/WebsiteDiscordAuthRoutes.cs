using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class WebsiteDiscordAuthRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/auth/discord/login", (HttpContext ctx, DiscordOAuthService oauth) =>
        {
            var state = Guid.NewGuid().ToString("N");
            var returnUrl = ctx.Request.Query["returnUrl"].ToString();
            if (string.IsNullOrWhiteSpace(returnUrl))
                returnUrl = "/portal/";

            var cookie = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10),
                IsEssential = true
            };

            ctx.Response.Cookies.Append("ow_oauth_state", state, cookie);
            ctx.Response.Cookies.Append("ow_portal_return_url", returnUrl, cookie);
            ctx.Response.Cookies.Append("ow_use_portal_callback", "1", cookie);

            return Results.Redirect(oauth.BuildAuthorizeUrl(state));
        });

        app.MapGet("/api/portal/me", (
            HttpContext ctx,
            WebSessionStore sessionStore,
            PortalDataStore portalStore,
            DiscordSocketClient discord) =>
        {
            var session = GetSession(ctx, sessionStore);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var guildId = ResolveGuildId(ctx, session, discord);
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "NoVtcMatched" }, statusCode: 404);

            var portal = portalStore.GetGuild(guildId);
            var discordGuild = ulong.TryParse(guildId, out var parsedGuildId)
                ? discord.GetGuild(parsedGuildId)
                : null;

            var currentMember = ResolveMember(discordGuild, session.DiscordUserId);
            var role = ResolveMemberRole(discordGuild, currentMember);
            var companyName = FirstNonBlank(portal.CompanyName, portal.SiteTitle, discordGuild?.Name, "Registered VTC");
            var about = FirstNonBlank(portal.CompanyInfo, portal.WelcomeText, "Owners and admins can write a brief company description here.");
            var roster = BuildRoster(discordGuild, portal);
            var fleet = BuildFleet(portal);
            var garages = BuildGarages(portal);
            var latest = BuildLatestInfo(discordGuild, portal);
            var stats = BuildStats(discordGuild, portal, roster.Count, fleet.Count, garages.Count);

            return Results.Json(new
            {
                ok = true,
                guildId,
                vtc = new
                {
                    guildId,
                    name = companyName,
                    description = FirstNonBlank(portal.WelcomeText, portal.CompanyInfo, $"Welcome to {companyName}."),
                    about,
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
                fleet,
                trucks = fleet,
                garages
            });
        });

        app.MapPost("/api/portal/about", async (
            HttpContext ctx,
            WebSessionStore sessionStore,
            PortalDataStore portalStore,
            DiscordSocketClient discord) =>
        {
            var session = GetSession(ctx, sessionStore);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var req = await ctx.Request.ReadFromJsonAsync<PortalAboutRequest>();
            var guildId = FirstNonBlank(req?.GuildId, ResolveGuildId(ctx, session, discord));
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            var discordGuild = ulong.TryParse(guildId, out var parsedGuildId)
                ? discord.GetGuild(parsedGuildId)
                : null;

            var member = ResolveMember(discordGuild, session.DiscordUserId);
            var role = ResolveMemberRole(discordGuild, member);
            if (role is not "Owner" and not "Admin" and not "Manager")
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            var about = req?.About?.Trim() ?? "";
            var updated = portalStore.UpdateGuild(guildId, g => g.CompanyInfo = about);
            return Results.Json(new { ok = true, about = updated.CompanyInfo });
        });

        app.MapGet("/api/auth/discord/callback", async (
            HttpContext ctx,
            DiscordOAuthService oauth,
            WebSessionStore sessionStore,
            VtcAccessService vtcAccess,
            CancellationToken ct) =>
        {
            var error = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrWhiteSpace(error))
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "discord_denied"));

            var code = ctx.Request.Query["code"].ToString();
            var state = ctx.Request.Query["state"].ToString();
            var expectedState = ctx.Request.Cookies["ow_oauth_state"];

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "invalid_state"));
            }

            var tokenRes = await oauth.ExchangeCodeAsync(code, ct);
            if (tokenRes == null || string.IsNullOrWhiteSpace(tokenRes.AccessToken))
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "token_failed"));

            var user = await oauth.GetCurrentUserAsync(tokenRes.AccessToken, ct);
            if (user == null || string.IsNullOrWhiteSpace(user.Id))
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "user_failed"));

            var guilds = await oauth.GetCurrentUserGuildsAsync(tokenRes.AccessToken, ct);
            var matches = vtcAccess.MatchSupportedVtcs(user.Id, guilds);
            var selectedGuildId = matches.FirstOrDefault()?.GuildId ?? "";
            var sessionId = Guid.NewGuid().ToString("N");

            sessionStore.Save(sessionId, new WebSessionUser
            {
                AccountId = "",
                Email = "",
                IsEmailAccount = false,
                DiscordUserId = user.Id,
                Username = user.Username,
                GlobalName = user.GlobalName,
                AccessToken = tokenRes.AccessToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            });

            ctx.Response.Cookies.Append("ow_session", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true
            });

            if (!string.IsNullOrWhiteSpace(selectedGuildId))
            {
                ctx.Response.Cookies.Append("ow_selected_guild", selectedGuildId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                    IsEssential = true
                });
            }

            ctx.Response.Cookies.Delete("ow_oauth_state");
            ctx.Response.Cookies.Delete("ow_use_portal_callback");
            ctx.Response.Cookies.Delete("ow_portal_return_url");

            return Results.Redirect(BuildPortalRedirect(ctx, sessionId, selectedGuildId, ""));
        });
    }

    private static WebSessionUser? GetSession(HttpContext ctx, WebSessionStore sessionStore)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                sessionId = auth[7..].Trim();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = ctx.Request.Headers["X-OverWatch-Session"].ToString();

        return !string.IsNullOrWhiteSpace(sessionId) && sessionStore.TryGet(sessionId, out var session) ? session : null;
    }

    private static string ResolveGuildId(HttpContext ctx, WebSessionUser session, DiscordSocketClient discord)
    {
        var requested = FirstNonBlank(
            ctx.Request.Query["guildId"].ToString(),
            ctx.Request.Query["vtcId"].ToString(),
            ctx.Request.Cookies["ow_selected_guild"]);

        if (!string.IsNullOrWhiteSpace(requested) && UserIsInGuild(discord, requested, session.DiscordUserId))
            return requested;

        if (!string.IsNullOrWhiteSpace(session.DiscordUserId))
        {
            foreach (var guild in discord.Guilds)
            {
                if (ResolveMember(guild, session.DiscordUserId) != null)
                    return guild.Id.ToString();
            }
        }

        return "";
    }

    private static bool UserIsInGuild(DiscordSocketClient discord, string guildId, string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(guildId, out var parsedGuildId))
            return false;

        var guild = discord.GetGuild(parsedGuildId);
        return ResolveMember(guild, discordUserId) != null;
    }

    private static SocketGuildUser? ResolveMember(SocketGuild? guild, string discordUserId)
    {
        if (guild == null || string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(discordUserId, out var parsedUserId))
            return null;

        return guild.GetUser(parsedUserId);
    }

    private static string ResolveMemberRole(SocketGuild? guild, SocketGuildUser? member)
    {
        if (guild == null || member == null)
            return "Driver";

        if (guild.OwnerId == member.Id)
            return "Owner";

        if (member.GuildPermissions.Administrator)
            return "Admin";

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
        var wanted = new[] { "Owner", "Admin", "Manager", "Dispatch", "Mechanic" };

        foreach (var roleName in wanted)
        {
            var names = new List<string>();

            if (guild != null)
            {
                names = guild.Users
                    .Where(u => !u.IsBot && ResolveMemberRole(guild, u) == roleName)
                    .Select(u => FirstNonBlank(u.DisplayName, u.GlobalName, u.Username))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();
            }

            if (names.Count == 0)
            {
                names = portal.ManagementTeam
                    .Where(x => string.Equals(x.Role, roleName, StringComparison.OrdinalIgnoreCase))
                    .Select(x => FirstNonBlank(x.Name, x.DiscordUsername))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Take(8)
                    .ToList();
            }

            rows.Add(new { role = roleName, userName = names.Count == 0 ? "Not assigned" : string.Join(", ", names) });
        }

        return rows;
    }

    private static List<object> BuildRoster(SocketGuild? guild, PortalGuildData portal)
    {
        var rows = new List<object>();
        var portalDriversByDiscord = portal.Drivers
            .Where(d => !string.IsNullOrWhiteSpace(d.DiscordUserId))
            .GroupBy(d => d.DiscordUserId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (guild != null)
        {
            foreach (var member in guild.Users.Where(u => !u.IsBot).OrderBy(u => u.DisplayName).Take(1000))
            {
                portalDriversByDiscord.TryGetValue(member.Id.ToString(), out var driver);
                var truck = ResolveTruckForDriver(portal, member.Id.ToString(), driver?.Name ?? member.DisplayName);
                var role = FirstNonBlank(driver?.Role, ResolveMemberRole(guild, member));

                rows.Add(new
                {
                    userName = FirstNonBlank(driver?.Name, member.DisplayName, member.GlobalName, member.Username, "Driver"),
                    role,
                    currentTruck = FirstNonBlank(driver?.AssignedTruck, driver?.FavoriteTruck, truck?.Name, truck?.Model, "Unassigned"),
                    totalMileage = FirstNonBlank(driver?.TotalMiles, driver?.Mileage, truck?.Odometer, "0"),
                    status = FirstNonBlank(driver?.Status, member.Status.ToString()),
                    awards = string.IsNullOrWhiteSpace(driver?.Achievement) ? Array.Empty<string>() : new[] { driver.Achievement },
                    location = FirstNonBlank(truck?.Location, "Unknown"),
                    driverScore = "N/A",
                    joined = FirstNonBlank(driver?.YearsInVtc, member.JoinedAt?.ToString("yyyy-MM-dd"), "N/A"),
                    discordUserId = member.Id.ToString(),
                    avatarUrl = member.GetDisplayAvatarUrl()
                });
            }
        }

        foreach (var driver in portal.Drivers.Where(d => string.IsNullOrWhiteSpace(d.DiscordUserId) || rows.All(r => !JsonContainsDiscordId(r, d.DiscordUserId))))
        {
            var truck = ResolveTruckForDriver(portal, driver.DiscordUserId, driver.Name);
            rows.Add(new
            {
                userName = FirstNonBlank(driver.Name, driver.DiscordUsername, "Driver"),
                role = FirstNonBlank(driver.Role, "Driver"),
                currentTruck = FirstNonBlank(driver.AssignedTruck, driver.FavoriteTruck, truck?.Name, truck?.Model, "Unassigned"),
                totalMileage = FirstNonBlank(driver.TotalMiles, driver.Mileage, truck?.Odometer, "0"),
                status = FirstNonBlank(driver.Status, truck?.Status, "Member"),
                awards = string.IsNullOrWhiteSpace(driver.Achievement) ? Array.Empty<string>() : new[] { driver.Achievement },
                location = FirstNonBlank(truck?.Location, "Unknown"),
                driverScore = "N/A",
                joined = FirstNonBlank(driver.YearsInVtc, "N/A"),
                discordUserId = driver.DiscordUserId,
                avatarUrl = driver.DiscordAvatarUrl
            });
        }

        return rows;
    }

    private static List<object> BuildFleet(PortalGuildData portal)
    {
        return portal.Trucks
            .OrderBy(t => PadTruckNumber(t.TruckNumber))
            .Select(t => new
            {
                id = t.Id,
                truckNumber = t.TruckNumber,
                name = FirstNonBlank(t.Name, t.Model, "Truck"),
                model = t.Model,
                driver = t.Driver,
                driverDiscordUserId = t.DriverDiscordUserId,
                plate = t.Plate,
                odometer = t.Odometer,
                location = t.Location,
                status = t.Status,
                condition = t.Condition,
                fuel = t.Fuel,
                notes = t.Notes
            })
            .Cast<object>()
            .ToList();
    }

    private static List<object> BuildGarages(PortalGuildData portal)
    {
        return portal.Garages
            .OrderByDescending(g => g.IsOwned)
            .ThenBy(g => FirstNonBlank(g.CityName, g.City))
            .Select(g => new
            {
                id = g.Id,
                cityName = FirstNonBlank(g.CityName, g.City, g.CityToken, "Garage"),
                city = FirstNonBlank(g.City, g.CityName, g.CityToken, "Garage"),
                state = g.State,
                country = g.Country,
                size = g.Size,
                slots = FirstNonBlank(g.Slots, g.TruckCapacity.ToString()),
                truckCapacity = g.TruckCapacity,
                isOwned = g.IsOwned,
                notes = g.Notes,
                assignedTruckNumbers = g.AssignedTruckNumbers
            })
            .Cast<object>()
            .ToList();
    }

    private static List<object> BuildLatestInfo(SocketGuild? guild, PortalGuildData portal)
    {
        var items = portal.LatestInfo
            .OrderByDescending(x => x.CreatedUtc)
            .Take(10)
            .Select(x => new { title = x.Title, body = x.Body, meta = FirstNonBlank(x.Meta, x.CreatedUtc.ToString("yyyy-MM-dd")) })
            .Cast<object>()
            .ToList();

        if (items.Count == 0)
        {
            items.Add(new
            {
                title = "Discord VTC Connected",
                body = guild == null
                    ? "The portal is connected, but Discord guild details were not cached yet. Keep the bot online and refresh."
                    : $"{guild.Name} is connected to the OverWatch ELD portal.",
                meta = "Live VTC"
            });

            items.Add(new
            {
                title = "Roster Sync",
                body = guild == null
                    ? "Roster data will show once Discord member cache is available."
                    : $"{guild.Users.Count(u => !u.IsBot)} Discord members are available to the portal roster.",
                meta = "Roster"
            });
        }

        return items;
    }

    private static object BuildStats(SocketGuild? guild, PortalGuildData portal, int rosterCount, int fleetCount, int garageCount)
    {
        var humans = guild?.Users.Where(u => !u.IsBot).ToList() ?? new List<SocketGuildUser>();
        var online = humans.Count(u => u.Status is UserStatus.Online or UserStatus.Idle or UserStatus.DoNotDisturb);
        var ownersAdminsManagers = humans.Count(u => ResolveMemberRole(guild, u) is "Owner" or "Admin" or "Manager");
        var availableTrucks = portal.Trucks.Count(t => string.IsNullOrWhiteSpace(t.Status) || t.Status.Contains("available", StringComparison.OrdinalIgnoreCase));
        var assignedTrucks = portal.Trucks.Count(t => !string.IsNullOrWhiteSpace(t.Driver) || !string.IsNullOrWhiteSpace(t.DriverDiscordUserId));

        return new
        {
            members = rosterCount > 0 ? rosterCount : humans.Count,
            discordMembers = humans.Count,
            onlineMembers = online,
            managementMembers = ownersAdminsManagers,
            fleetTrucks = fleetCount,
            assignedTrucks,
            availableTrucks,
            garages = garageCount,
            ownedGarages = portal.Garages.Count(g => g.IsOwned),
            latestInfo = portal.LatestInfo.Count,
            updatedUtc = portal.UpdatedUtc
        };
    }

    private static PortalTruck? ResolveTruckForDriver(PortalGuildData portal, string? discordUserId, string? driverName)
    {
        return portal.Trucks.FirstOrDefault(t =>
            (!string.IsNullOrWhiteSpace(discordUserId) && string.Equals(t.DriverDiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(driverName) && string.Equals(t.Driver, driverName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool JsonContainsDiscordId(object row, string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId))
            return false;

        return row.ToString()?.Contains(discordUserId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string PadTruckNumber(string value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? value : digits.PadLeft(10, '0');
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static string BuildPortalRedirect(HttpContext ctx, string token, string guildId, string error)
    {
        var portalBase = (Environment.GetEnvironmentVariable("OVERWATCH_PORTAL_BASE_URL")
            ?? Environment.GetEnvironmentVariable("PUBLIC_PORTAL_BASE_URL")
            ?? "https://overwatcheld.com")
            .Trim()
            .TrimEnd('/');

        var returnUrl = ctx.Request.Cookies["ow_portal_return_url"];
        if (string.IsNullOrWhiteSpace(returnUrl))
            returnUrl = "/portal/";

        if (!returnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!returnUrl.StartsWith("/"))
                returnUrl = "/" + returnUrl.TrimStart('.', '/');
            returnUrl = portalBase + returnUrl;
        }

        var separator = returnUrl.Contains('?') ? "&" : "?";
        if (!string.IsNullOrWhiteSpace(error))
            return returnUrl + separator + "error=" + Uri.EscapeDataString(error);

        return returnUrl + separator + "token=" + Uri.EscapeDataString(token) + "&guildId=" + Uri.EscapeDataString(guildId);
    }

    private sealed class PortalAboutRequest
    {
        public string GuildId { get; set; } = "";
        public string About { get; set; } = "";
    }
}
