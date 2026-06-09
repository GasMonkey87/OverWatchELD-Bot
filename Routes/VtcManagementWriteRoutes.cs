using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcManagementWriteRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapPost("/api/vtc/admin/{guildId}/roster/role", async (string guildId, HttpContext ctx, PortalDataStore store, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<RoleRequest>() ?? new RoleRequest();
            PortalDriver? driver = null;
            store.UpdateGuild(guildId, g =>
            {
                driver = FindDriver(g, req.UserId, req.Name, true);
                driver!.Name = First(req.Name, driver.Name, "Driver");
                driver.DiscordUserId = First(req.UserId, driver.DiscordUserId);
                driver.Role = First(req.Role, driver.Role, "Driver");
                Log(g, "Role Updated", driver.Name + " set to " + driver.Role + " by " + access.DisplayName);
            });
            return Results.Json(new { ok = true, driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/roster/truck", async (string guildId, HttpContext ctx, PortalDataStore store, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<TruckAssignRequest>() ?? new TruckAssignRequest();
            PortalDriver? driver = null; PortalTruck? truck = null;
            store.UpdateGuild(guildId, g =>
            {
                driver = FindDriver(g, req.UserId, req.Name, true);
                driver!.Name = First(req.Name, driver.Name, "Driver");
                driver.DiscordUserId = First(req.UserId, driver.DiscordUserId);
                driver.AssignedTruck = First(req.TruckName, req.TruckNumber, driver.AssignedTruck);
                truck = FindTruck(g, req.TruckId, req.TruckNumber, req.TruckName, true);
                truck!.TruckNumber = First(req.TruckNumber, truck.TruckNumber);
                truck.Name = First(req.TruckName, truck.Name, truck.Model);
                truck.Driver = driver.Name;
                truck.DriverDiscordUserId = driver.DiscordUserId;
                truck.Status = "Assigned";
                Log(g, "Truck Assigned", driver.Name + " assigned to " + First(truck.TruckNumber, truck.Name));
            });
            return Results.Json(new { ok = true, driver, truck });
        });
    }

    private static PortalDriver FindDriver(PortalGuildData g, string userId, string name, bool create)
    {
        var d = g.Drivers.FirstOrDefault(x => Same(x.DiscordUserId, userId) || Same(x.Name, name) || Same(x.DiscordUsername, name));
        if (d == null && create) { d = new PortalDriver { Name = First(name, "Driver"), DiscordUserId = userId ?? "", Role = "Driver", Status = "Member" }; g.Drivers.Add(d); }
        return d!;
    }

    private static PortalTruck FindTruck(PortalGuildData g, string id, string number, string name, bool create)
    {
        var t = g.Trucks.FirstOrDefault(x => Same(x.Id, id) || Same(x.TruckNumber, number) || Same(x.Name, name));
        if (t == null && create) { t = new PortalTruck { TruckNumber = number ?? "", Name = name ?? "", Status = "Available" }; g.Trucks.Add(t); }
        return t!;
    }

    private static void Log(PortalGuildData g, string title, string body)
    {
        g.LatestInfo.Insert(0, new PortalLatestInfo { Title = title, Body = body, Meta = "Admin", CreatedUtc = DateTimeOffset.UtcNow });
        if (g.LatestInfo.Count > 250) g.LatestInfo = g.LatestInfo.Take(250).ToList();
    }

    private static AccessResult CheckAccess(HttpContext ctx, WebSessionStore sessions, DiscordSocketClient discord, string guildId)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessionId = auth[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = ctx.Request.Headers["X-OverWatch-Session"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId) || !sessions.TryGet(sessionId, out var session) || session == null) return new AccessResult(false, "NotAuthenticated", 401, "");
        if (!ulong.TryParse(guildId, out var gid) || !ulong.TryParse(session.DiscordUserId, out var uid)) return new AccessResult(false, "Forbidden", 403, "");
        var guild = discord.GetGuild(gid); var user = guild?.GetUser(uid); if (guild == null || user == null) return new AccessResult(false, "Forbidden", 403, "");
        var role = guild.OwnerId == user.Id ? "Owner" : user.GuildPermissions.Administrator ? "Admin" : user.Roles.Any(r => r.Name.Contains("manager", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("management", StringComparison.OrdinalIgnoreCase)) ? "Manager" : "Driver";
        return role is "Owner" or "Admin" or "Manager" ? new AccessResult(true, "", 200, First(user.DisplayName, user.GlobalName, user.Username)) : new AccessResult(false, "Forbidden", 403, "");
    }

    private static string First(params string?[] values) { foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v.Trim(); return ""; }
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private sealed record AccessResult(bool Ok, string Error, int StatusCode, string DisplayName);
    private sealed class RoleRequest { public string UserId { get; set; } = ""; public string Name { get; set; } = ""; public string Role { get; set; } = "Driver"; }
    private sealed class TruckAssignRequest { public string UserId { get; set; } = ""; public string Name { get; set; } = ""; public string TruckId { get; set; } = ""; public string TruckNumber { get; set; } = ""; public string TruckName { get; set; } = ""; }
}
