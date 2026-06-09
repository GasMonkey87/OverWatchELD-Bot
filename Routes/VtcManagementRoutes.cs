using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcManagementRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/vtc/admin/{guildId}/audit", (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var portal = portalStore.GetGuild(guildId);
            var rows = portal.AuditLog.OrderByDescending(x => x.CreatedUtc).Take(100).Select(x => new { x.Id, x.Action, x.Detail, x.Actor, x.ActorDiscordUserId, x.CreatedUtc }).ToList();
            return Results.Json(new { ok = true, audit = rows });
        });

        app.MapPost("/api/vtc/admin/{guildId}/applications/{applicationId}/interview", async (string guildId, string applicationId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<NotesRequest>() ?? new NotesRequest();
            var updated = false;
            portalStore.UpdateGuild(guildId, g =>
            {
                var row = g.Applications.FirstOrDefault(a => a.Id == applicationId);
                if (row == null) return;
                row.Status = "Interview";
                row.ReviewNotes = req.Notes.Trim();
                row.ReviewedBy = access.DisplayName;
                row.ReviewedUtc = DateTimeOffset.UtcNow;
                AddAudit(g, "Application marked for interview", row.ApplicantName, access);
                updated = true;
            });
            return updated ? Results.Json(new { ok = true, status = "Interview" }) : Results.Json(new { ok = false, error = "ApplicationNotFound" }, statusCode: 404);
        });

        app.MapPost("/api/vtc/admin/{guildId}/applications/{applicationId}/approve-driver", async (string guildId, string applicationId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<ApproveRequest>() ?? new ApproveRequest();
            var updated = false;
            portalStore.UpdateGuild(guildId, g =>
            {
                var appRow = g.Applications.FirstOrDefault(a => a.Id == applicationId);
                if (appRow == null) return;
                appRow.Status = "Approved";
                appRow.ReviewNotes = req.Notes.Trim();
                appRow.ReviewedBy = access.DisplayName;
                appRow.ReviewedUtc = DateTimeOffset.UtcNow;
                var driver = FindDriver(g, appRow.ApplicantDiscordUserId, appRow.ApplicantDiscord, appRow.ApplicantName);
                if (driver == null)
                {
                    driver = new PortalDriver();
                    g.Drivers.Add(driver);
                }
                driver.Name = FirstNonBlank(req.DriverName, appRow.ApplicantName, driver.Name);
                driver.DiscordUsername = FirstNonBlank(appRow.ApplicantDiscord, driver.DiscordUsername);
                driver.DiscordUserId = FirstNonBlank(appRow.ApplicantDiscordUserId, driver.DiscordUserId);
                driver.Role = FirstNonBlank(req.Role, "Driver");
                driver.AssignedTruck = req.AssignedTruck.Trim();
                driver.Status = "Approved";
                AddAudit(g, "Application approved and driver added", driver.Name, access);
                updated = true;
            });
            return updated ? Results.Json(new { ok = true, status = "Approved" }) : Results.Json(new { ok = false, error = "ApplicationNotFound" }, statusCode: 404);
        });

        app.MapPost("/api/vtc/admin/{guildId}/roster/role", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<RoleRequest>() ?? new RoleRequest();
            if (string.IsNullOrWhiteSpace(req.DriverName) && string.IsNullOrWhiteSpace(req.DiscordUserId)) return Results.Json(new { ok = false, error = "MissingDriver" }, statusCode: 400);
            PortalDriver? driver = null;
            portalStore.UpdateGuild(guildId, g =>
            {
                driver = FindDriver(g, req.DiscordUserId, "", req.DriverName) ?? new PortalDriver { Name = req.DriverName.Trim(), DiscordUserId = req.DiscordUserId.Trim() };
                if (!g.Drivers.Contains(driver)) g.Drivers.Add(driver);
                driver.Role = FirstNonBlank(req.Role, "Driver");
                AddAudit(g, "Driver role updated", $"{driver.Name} -> {driver.Role}", access);
            });
            return Results.Json(new { ok = true, driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/roster/truck", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<AssignTruckRequest>() ?? new AssignTruckRequest();
            PortalDriver? driver = null;
            portalStore.UpdateGuild(guildId, g =>
            {
                driver = FindDriver(g, req.DiscordUserId, "", req.DriverName) ?? new PortalDriver { Name = req.DriverName.Trim(), DiscordUserId = req.DiscordUserId.Trim() };
                if (!g.Drivers.Contains(driver)) g.Drivers.Add(driver);
                driver.AssignedTruck = req.TruckName.Trim();
                var truck = FindTruck(g, req.TruckId, req.TruckNumber, req.TruckName);
                if (truck == null && (!string.IsNullOrWhiteSpace(req.TruckName) || !string.IsNullOrWhiteSpace(req.TruckNumber)))
                {
                    truck = new PortalTruck { Name = req.TruckName.Trim(), TruckNumber = req.TruckNumber.Trim() };
                    g.Trucks.Add(truck);
                }
                if (truck != null)
                {
                    truck.Driver = driver.Name;
                    truck.DriverDiscordUserId = driver.DiscordUserId;
                    if (!string.IsNullOrWhiteSpace(req.TruckName)) truck.Name = req.TruckName.Trim();
                    if (!string.IsNullOrWhiteSpace(req.TruckNumber)) truck.TruckNumber = req.TruckNumber.Trim();
                    truck.Status = "Assigned";
                }
                AddAudit(g, "Truck assigned", $"{driver.Name} -> {driver.AssignedTruck}", access);
            });
            return Results.Json(new { ok = true, driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/awards", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<AwardRequest>() ?? new AwardRequest();
            PortalDriver? driver = null;
            portalStore.UpdateGuild(guildId, g =>
            {
                driver = FindDriver(g, req.DiscordUserId, "", req.DriverName) ?? new PortalDriver { Name = req.DriverName.Trim(), DiscordUserId = req.DiscordUserId.Trim() };
                if (!g.Drivers.Contains(driver)) g.Drivers.Add(driver);
                driver.Achievement = req.AwardName.Trim();
                if (!g.FeaturedDrivers.Any(x => string.Equals(x.Name, driver.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Achievement, req.AwardName, StringComparison.OrdinalIgnoreCase)))
                {
                    g.FeaturedDrivers.Add(new PortalDriver { Name = driver.Name, DiscordUserId = driver.DiscordUserId, DiscordUsername = driver.DiscordUsername, Achievement = req.AwardName.Trim(), Bio = req.Notes.Trim(), Role = driver.Role });
                }
                AddAudit(g, "Award assigned", $"{driver.Name} -> {req.AwardName}", access);
            });
            return Results.Json(new { ok = true, driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/trucks/save", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<TruckRequest>() ?? new TruckRequest();
            PortalTruck? truck = null;
            portalStore.UpdateGuild(guildId, g =>
            {
                truck = FindTruck(g, req.Id, req.TruckNumber, req.Name) ?? new PortalTruck();
                if (!g.Trucks.Contains(truck)) g.Trucks.Add(truck);
                if (!string.IsNullOrWhiteSpace(req.TruckNumber)) truck.TruckNumber = req.TruckNumber.Trim();
                truck.Name = req.Name.Trim();
                truck.Model = req.Model.Trim();
                truck.Driver = req.Driver.Trim();
                truck.DriverDiscordUserId = req.DriverDiscordUserId.Trim();
                truck.Plate = req.Plate.Trim();
                truck.Odometer = req.Odometer.Trim();
                truck.Location = req.Location.Trim();
                truck.Status = FirstNonBlank(req.Status, truck.Status, "Available");
                truck.Condition = req.Condition.Trim();
                truck.Fuel = req.Fuel.Trim();
                truck.Notes = req.Notes.Trim();
                AddAudit(g, "Truck saved", FirstNonBlank(truck.TruckNumber, truck.Name, truck.Model), access);
            });
            return Results.Json(new { ok = true, truck });
        });

        app.MapPost("/api/vtc/admin/{guildId}/garages/save", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<GarageRequest>() ?? new GarageRequest();
            PortalGarage? garage = null;
            portalStore.UpdateGuild(guildId, g =>
            {
                garage = g.Garages.FirstOrDefault(x => x.Id == req.Id) ?? g.Garages.FirstOrDefault(x => string.Equals(FirstNonBlank(x.CityName, x.City), FirstNonBlank(req.CityName, req.City), StringComparison.OrdinalIgnoreCase) && string.Equals(x.State, req.State, StringComparison.OrdinalIgnoreCase)) ?? new PortalGarage();
                if (!g.Garages.Contains(garage)) g.Garages.Add(garage);
                garage.CityName = FirstNonBlank(req.CityName, req.City);
                garage.City = FirstNonBlank(req.City, req.CityName);
                garage.State = req.State.Trim();
                garage.Country = req.Country.Trim();
                garage.Slots = req.Slots.Trim();
                garage.Size = FirstNonBlank(req.Size, garage.Size, "Small");
                garage.TruckCapacity = req.TruckCapacity <= 0 ? garage.TruckCapacity : req.TruckCapacity;
                garage.IsOwned = req.IsOwned;
                garage.Notes = req.Notes.Trim();
                AddAudit(g, "Garage saved", FirstNonBlank(garage.CityName, garage.City), access);
            });
            return Results.Json(new { ok = true, garage });
        });
    }

    private static AccessResult CheckManagerAccess(HttpContext ctx, WebSessionStore sessions, DiscordSocketClient discord, string guildId)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessionId = auth[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = ctx.Request.Headers["X-OverWatch-Session"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId) || !sessions.TryGet(sessionId, out var session) || session == null) return new AccessResult(false, "NotAuthenticated", 401, "", "");
        if (!ulong.TryParse(guildId, out var parsedGuildId) || !ulong.TryParse(session.DiscordUserId, out var parsedUserId)) return new AccessResult(false, "Forbidden", 403, "", "");
        var guild = discord.GetGuild(parsedGuildId);
        var user = guild?.GetUser(parsedUserId);
        if (guild == null || user == null) return new AccessResult(false, "Forbidden", 403, "", "");
        var role = ResolveMemberRole(guild, user);
        if (role is not "Owner" and not "Admin" and not "Manager") return new AccessResult(false, "Forbidden", 403, "", "");
        return new AccessResult(true, "", 200, FirstNonBlank(user.DisplayName, user.GlobalName, user.Username), user.Id.ToString());
    }

    private static string ResolveMemberRole(SocketGuild guild, SocketGuildUser user)
    {
        if (guild.OwnerId == user.Id) return "Owner";
        if (user.GuildPermissions.Administrator) return "Admin";
        var roles = user.Roles.Select(r => r.Name.ToLowerInvariant()).ToList();
        if (roles.Any(r => r.Contains("owner"))) return "Owner";
        if (roles.Any(r => r.Contains("admin"))) return "Admin";
        if (roles.Any(r => r.Contains("manager") || r.Contains("management"))) return "Manager";
        return "Driver";
    }

    private static PortalDriver? FindDriver(PortalGuildData g, string? discordUserId, string? discordUsername, string? name)
    {
        return g.Drivers.FirstOrDefault(d => (!string.IsNullOrWhiteSpace(discordUserId) && string.Equals(d.DiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(discordUsername) && string.Equals(d.DiscordUsername, discordUsername, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(name) && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static PortalTruck? FindTruck(PortalGuildData g, string? id, string? number, string? name)
    {
        return g.Trucks.FirstOrDefault(t => (!string.IsNullOrWhiteSpace(id) && t.Id == id) || (!string.IsNullOrWhiteSpace(number) && string.Equals(t.TruckNumber, number, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(name) && (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Model, name, StringComparison.OrdinalIgnoreCase))));
    }

    private static void AddAudit(PortalGuildData g, string action, string detail, AccessResult access)
    {
        g.AuditLog.Add(new PortalAuditEntry { Action = action, Detail = detail, Actor = access.DisplayName, ActorDiscordUserId = access.DiscordUserId, CreatedUtc = DateTimeOffset.UtcNow });
        if (g.AuditLog.Count > 250) g.AuditLog = g.AuditLog.OrderByDescending(x => x.CreatedUtc).Take(250).OrderBy(x => x.CreatedUtc).ToList();
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    private sealed record AccessResult(bool Ok, string Error, int StatusCode, string DisplayName, string DiscordUserId);
    private sealed class NotesRequest { public string Notes { get; set; } = ""; }
    private sealed class ApproveRequest { public string Notes { get; set; } = ""; public string DriverName { get; set; } = ""; public string Role { get; set; } = "Driver"; public string AssignedTruck { get; set; } = ""; }
    private sealed class RoleRequest { public string DriverName { get; set; } = ""; public string DiscordUserId { get; set; } = ""; public string Role { get; set; } = "Driver"; }
    private sealed class AssignTruckRequest { public string DriverName { get; set; } = ""; public string DiscordUserId { get; set; } = ""; public string TruckId { get; set; } = ""; public string TruckNumber { get; set; } = ""; public string TruckName { get; set; } = ""; }
    private sealed class AwardRequest { public string DriverName { get; set; } = ""; public string DiscordUserId { get; set; } = ""; public string AwardName { get; set; } = ""; public string Notes { get; set; } = ""; }
    private sealed class TruckRequest { public string Id { get; set; } = ""; public string TruckNumber { get; set; } = ""; public string Name { get; set; } = ""; public string Model { get; set; } = ""; public string Driver { get; set; } = ""; public string DriverDiscordUserId { get; set; } = ""; public string Plate { get; set; } = ""; public string Odometer { get; set; } = ""; public string Location { get; set; } = ""; public string Status { get; set; } = ""; public string Condition { get; set; } = ""; public string Fuel { get; set; } = ""; public string Notes { get; set; } = ""; }
    private sealed class GarageRequest { public string Id { get; set; } = ""; public string City { get; set; } = ""; public string CityName { get; set; } = ""; public string State { get; set; } = ""; public string Country { get; set; } = ""; public string Slots { get; set; } = ""; public string Size { get; set; } = ""; public int TruckCapacity { get; set; } public bool IsOwned { get; set; } = true; public string Notes { get; set; } = ""; }
}