using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcSelectionRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/portal/vtcs", (HttpContext ctx, WebSessionStore sessions, PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var session = GetSession(ctx, sessions);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var selectedGuildId = FirstNonBlank(ctx.Request.Query["guildId"].ToString(), ctx.Request.Cookies["ow_selected_guild"]);
            var vtcs = BuildVtcChoices(session, portalStore, discord);

            if (!string.IsNullOrWhiteSpace(selectedGuildId) && vtcs.All(v => v.GuildId != selectedGuildId))
                selectedGuildId = "";

            return Results.Json(new
            {
                ok = true,
                selectedGuildId,
                count = vtcs.Count,
                requiresSelection = vtcs.Count != 1 || string.IsNullOrWhiteSpace(selectedGuildId),
                isEmailAccount = session.IsEmailAccount,
                discordLinked = !string.IsNullOrWhiteSpace(session.DiscordUserId),
                vtcs = vtcs.Select(v => new
                {
                    guildId = v.GuildId,
                    name = v.Name,
                    description = v.Description,
                    logoUrl = v.LogoUrl,
                    bannerUrl = v.BannerUrl,
                    memberCount = v.MemberCount,
                    truckCount = v.TruckCount,
                    garageCount = v.GarageCount,
                    role = v.Role,
                    accessType = v.AccessType,
                    canEditPortal = v.CanEditPortal
                })
            });
        });

        app.MapPost("/api/portal/select-vtc", async (HttpContext ctx, WebSessionStore sessions, PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var session = GetSession(ctx, sessions);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var req = await ctx.Request.ReadFromJsonAsync<SelectVtcRequest>() ?? new SelectVtcRequest();
            if (string.IsNullOrWhiteSpace(req.GuildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            var vtcs = BuildVtcChoices(session, portalStore, discord);
            if (vtcs.All(v => v.GuildId != req.GuildId))
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            ctx.Response.Cookies.Append("ow_selected_guild", req.GuildId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true
            });

            return Results.Json(new { ok = true, guildId = req.GuildId, redirectUrl = "/portal/?guildId=" + Uri.EscapeDataString(req.GuildId) });
        });
    }

    private static List<VtcChoice> BuildVtcChoices(WebSessionUser session, PortalDataStore portalStore, DiscordSocketClient discord)
    {
        var root = portalStore.Load();
        var rows = new Dictionary<string, VtcChoice>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(session.DiscordUserId))
        {
            foreach (var guild in discord.Guilds)
            {
                var member = ResolveMember(guild, session.DiscordUserId);
                if (member == null) continue;

                var guildId = guild.Id.ToString();
                root.Guilds.TryGetValue(guildId, out var portal);
                portal ??= new PortalGuildData { GuildId = guildId, CompanyName = guild.Name, LogoImageUrl = guild.IconUrl ?? "" };
                rows[guildId] = BuildChoice(guildId, guild, portal, ResolveMemberRole(guild, member), "Discord Member");
            }
        }

        if (rows.Count == 0 && session.IsEmailAccount)
        {
            foreach (var portal in root.Guilds.Values.Where(g => g.IsPublicDirectoryListed))
            {
                var guild = ulong.TryParse(portal.GuildId, out var parsedGuildId) ? discord.GetGuild(parsedGuildId) : null;
                rows[portal.GuildId] = BuildChoice(portal.GuildId, guild, portal, "Applicant", "Email Login");
            }

            foreach (var guild in discord.Guilds)
            {
                var guildId = guild.Id.ToString();
                if (rows.ContainsKey(guildId)) continue;
                var portal = new PortalGuildData
                {
                    GuildId = guildId,
                    CompanyName = guild.Name,
                    WelcomeText = $"{guild.Name} is registered with OverWatch ELD.",
                    LogoImageUrl = guild.IconUrl ?? "",
                    IsPublicDirectoryListed = true
                };
                rows[guildId] = BuildChoice(guildId, guild, portal, "Applicant", "Email Login");
            }
        }

        return rows.Values.OrderBy(v => v.Name).ToList();
    }

    private static VtcChoice BuildChoice(string guildId, SocketGuild? guild, PortalGuildData portal, string role, string accessType)
    {
        var name = FirstNonBlank(portal.CompanyName, portal.SiteTitle, guild?.Name, "Registered VTC");
        var logo = FirstNonBlank(portal.LogoImageUrl, guild?.IconUrl, "");
        var banner = FirstNonBlank(portal.BannerImageUrl, portal.HeroImageUrl, portal.CompanyPictureUrl, "");
        return new VtcChoice
        {
            GuildId = guildId,
            Name = name,
            Description = FirstNonBlank(portal.PublicRecruitingMessage, portal.WelcomeText, portal.CompanyInfo, $"Welcome to {name}."),
            LogoUrl = logo,
            BannerUrl = banner,
            MemberCount = guild?.Users.Count(u => !u.IsBot) ?? portal.Drivers.Count,
            TruckCount = portal.Trucks.Count,
            GarageCount = portal.Garages.Count,
            Role = role,
            AccessType = accessType,
            CanEditPortal = role is "Owner" or "Admin" or "Manager"
        };
    }

    private static WebSessionUser? GetSession(HttpContext ctx, WebSessionStore sessions)
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

        return !string.IsNullOrWhiteSpace(sessionId) && sessions.TryGet(sessionId, out var session) ? session : null;
    }

    private static SocketGuildUser? ResolveMember(SocketGuild guild, string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(discordUserId, out var userId))
            return null;
        return guild.GetUser(userId);
    }

    private static string ResolveMemberRole(SocketGuild guild, SocketGuildUser member)
    {
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

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    private sealed class SelectVtcRequest
    {
        public string GuildId { get; set; } = "";
    }

    private sealed class VtcChoice
    {
        public string GuildId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string LogoUrl { get; set; } = "";
        public string BannerUrl { get; set; } = "";
        public int MemberCount { get; set; }
        public int TruckCount { get; set; }
        public int GarageCount { get; set; }
        public string Role { get; set; } = "Driver";
        public string AccessType { get; set; } = "";
        public bool CanEditPortal { get; set; }
    }
}
