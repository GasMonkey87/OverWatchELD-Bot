using System.Text.Json;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Models;

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

            var locks = LoadLocks();
            var accountKey = GetAccountKey(session);
            locks.TryGetValue(accountKey, out var locked);

            var selectedGuildId = FirstNonBlank(ctx.Request.Query["guildId"].ToString(), ctx.Request.Cookies["ow_selected_guild"], locked?.GuildId);
            var vtcs = BuildVtcChoices(session, portalStore, discord);

            if (session.IsEmailAccount && locked != null)
            {
                vtcs = vtcs.Where(v => string.Equals(v.GuildId, locked.GuildId, StringComparison.Ordinal)).ToList();
                if (vtcs.Count == 0)
                {
                    var root = portalStore.Load();
                    root.Guilds.TryGetValue(locked.GuildId, out var portal);
                    var guild = ulong.TryParse(locked.GuildId, out var parsedGuildId) ? discord.GetGuild(parsedGuildId) : null;
                    portal ??= new PortalGuildData { GuildId = locked.GuildId, CompanyName = FirstNonBlank(locked.VtcName, guild?.Name, "Locked VTC") };
                    vtcs.Add(BuildChoice(locked.GuildId, guild, portal, "Driver", "Locked Email VTC"));
                }
                selectedGuildId = locked.GuildId;
            }

            if (!string.IsNullOrWhiteSpace(selectedGuildId) && vtcs.All(v => v.GuildId != selectedGuildId))
                selectedGuildId = "";

            return Results.Json(new
            {
                ok = true,
                selectedGuildId,
                count = vtcs.Count,
                requiresSelection = session.IsEmailAccount && locked != null ? false : (vtcs.Count != 1 || string.IsNullOrWhiteSpace(selectedGuildId)),
                isEmailAccount = session.IsEmailAccount,
                discordLinked = !string.IsNullOrWhiteSpace(session.DiscordUserId),
                lockedToVtc = session.IsEmailAccount && locked != null,
                canChangeVtc = !(session.IsEmailAccount && locked != null),
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

            var locks = LoadLocks();
            var accountKey = GetAccountKey(session);
            if (session.IsEmailAccount && locks.TryGetValue(accountKey, out var existing) && !string.Equals(existing.GuildId, req.GuildId, StringComparison.Ordinal))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "VtcLocked",
                    message = "This email account is already locked to a VTC. Leave the current VTC from the portal before selecting another.",
                    guildId = existing.GuildId,
                    vtcName = existing.VtcName
                }, statusCode: 409);
            }

            var vtcs = BuildVtcChoices(session, portalStore, discord);
            var selected = vtcs.FirstOrDefault(v => v.GuildId == req.GuildId);
            if (selected == null)
                return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

            if (session.IsEmailAccount && !locks.ContainsKey(accountKey))
            {
                locks[accountKey] = new EmailVtcLock
                {
                    AccountKey = accountKey,
                    GuildId = selected.GuildId,
                    VtcName = selected.Name,
                    LockedUtc = DateTimeOffset.UtcNow
                };
                SaveLocks(locks);
            }

            ctx.Response.Cookies.Append("ow_selected_guild", req.GuildId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true
            });

            return Results.Json(new { ok = true, guildId = req.GuildId, lockedToVtc = session.IsEmailAccount, redirectUrl = "/portal/?guildId=" + Uri.EscapeDataString(req.GuildId) });
        });

        app.MapPost("/api/portal/leave-vtc", (HttpContext ctx, WebSessionStore sessions) =>
        {
            var session = GetSession(ctx, sessions);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var locks = LoadLocks();
            var accountKey = GetAccountKey(session);
            var removed = session.IsEmailAccount && locks.Remove(accountKey);
            if (removed)
                SaveLocks(locks);

            ctx.Response.Cookies.Delete("ow_selected_guild");
            return Results.Json(new { ok = true, removed, redirectUrl = "/select-vtc/" });
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

    private static Dictionary<string, EmailVtcLock> LoadLocks()
    {
        try
        {
            var path = LockPath();
            if (!File.Exists(path)) return new Dictionary<string, EmailVtcLock>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, EmailVtcLock>>(File.ReadAllText(path)) ?? new Dictionary<string, EmailVtcLock>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, EmailVtcLock>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveLocks(Dictionary<string, EmailVtcLock> locks)
    {
        var path = LockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(locks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string LockPath() => Path.Combine(AppContext.BaseDirectory, "data", "email_vtc_locks.json");
    private static string GetAccountKey(WebSessionUser session) => FirstNonBlank(session.AccountId, session.Email, session.Username).Trim().ToLowerInvariant();

    private static SocketGuildUser? ResolveMember(SocketGuild guild, string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(discordUserId, out var userId)) return null;
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
        foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    private sealed class SelectVtcRequest { public string GuildId { get; set; } = ""; }
    private sealed class EmailVtcLock
    {
        public string AccountKey { get; set; } = "";
        public string GuildId { get; set; } = "";
        public string VtcName { get; set; } = "";
        public DateTimeOffset LockedUtc { get; set; }
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
