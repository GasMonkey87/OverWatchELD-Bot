using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcManagementAssetRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapPost("/api/vtc/admin/{guildId}/awards", async (string guildId, HttpContext ctx, PortalDataStore store, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<AwardRequest>() ?? new AwardRequest();
            PortalDriver? driver = null;
            store.UpdateGuild(guildId, g =>
            {
                driver = FindDriver(g, req.UserId, req.Name, true);
                driver!.Name = First(req.Name, driver.Name, "Driver");
                driver.Achievement = First(req.Award, driver.Achievement);
                driver.Bio = First(req.Notes, driver.Bio);
                if (!g.FeaturedDrivers.Any(d => Same(d.Id, driver.Id) || Same(d.DiscordUserId, driver.DiscordUserId) || Same(d.Name, driver.Name))) g.FeaturedDrivers.Add(driver);
                Log(g, "Award Assigned", driver.Name + " received " + driver.Achievement);
            });
            return Results.Json(new { ok = true, award = driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/trucks", async (string guildId, HttpContext ctx, PortalDataStore store, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<TruckRequest>() ?? new TruckRequest();
            PortalTruck? truck = null;
            store.UpdateGuild(guildId, g =>
            {
                truck = FindTruck(g, req.Id, req.Number, req.Name, true);
                truck!.TruckNumber = First(req.Number, truck.TruckNumber);
                truck.Name = First(req.Name, truck.Name);
                truck.Model = First(req.Model, truck.Model, req.Name);
                truck.Driver = First(req.Driver, truck.Driver);
                truck.Plate = First(req.Plate, truck.Plate);
                truck.Odometer = First(req.Odometer, truck.Odometer);
                truck.Location = First(req.Location, truck.Location);
                truck.Status = First(req.Status, truck.Status, "Available");
                truck.Condition = First(req.Condition, truck.Condition);
                truck.Fuel = First(req.Fuel, truck.Fuel);
                truck.Notes = First(req.Notes, truck.Notes);
                Log(g, "Truck Saved", First(truck.TruckNumber, truck.Name, truck.Model) + " saved by " + access.DisplayName);
            });
            return Results.Json(new { ok = true, truck });
        });

        app.MapPost("/api/vtc/admin/{guildId}/garages", async (string guildId, HttpContext ctx, PortalDataStore store, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<GarageRequest>() ?? new GarageRequest();
            PortalGarage? garage = null;
            store.UpdateGuild(guildId, g =>
            {
                garage = string.IsNullOrWhiteSpace(req.Id) ? null : g.Garages.FirstOrDefault(x => x.Id == req.Id);
                if (garage == null) { garage = new PortalGarage(); g.Garages.Add(garage); }
                garage.City = First(req.City, garage.City);
                garage.CityName = First(req.CityName, garage.CityName, req.City);
                garage.State = First(req.State, garage.State);
                garage.Country = First(req.Country, garage.Country, "USA");
                garage.Slots = First(req.Slots, garage.Slots);
                garage.Size = First(req.Size, garage.Size, "Small");
                garage.TruckCapacity = req.Capacity > 0 ? req.Capacity : garage.TruckCapacity;
                garage.IsOwned = req.Owned || garage.IsOwned;
                garage.Notes = First(req.Notes, garage.Notes);
                Log(g, "Garage Saved", First(garage.CityName, garage.City, "Garage") + " saved by " + access.DisplayName);
            });
            return Results.Json(new { ok = true, garage });
        });

        app.MapGet("/api/vtc/admin/{guildId}/audit", (string guildId, HttpContext ctx, PortalDataStore store, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var audit = store.GetGuild(guildId).LatestInfo.OrderByDescending(x => x.CreatedUtc).Take(100).Select(x => new { x.Id, title = x.Title, body = x.Body, meta = x.Meta, createdUtc = x.CreatedUtc }).ToList();
            return Results.Json(new { ok = true, audit });
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
    private static void Log(PortalGuildData g, string title, string body) { g.LatestInfo.Insert(0, new PortalLatestInfo { Title = title, Body = body, Meta = "Admin", CreatedUtc = DateTimeOffset.UtcNow }); if (g.LatestInfo.Count > 250) g.LatestInfo = g.LatestInfo.Take(250).ToList(); }
    private static AccessResult CheckAccess(HttpContext ctx, WebSessionStore sessions, DiscordSocketClient discord, string guildId)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId)) { var auth = ctx.Request.Headers.Authorization.ToString(); if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessionId = auth[7..].Trim(); }
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
    private sealed class AwardRequest { public string UserId { get; set; } = ""; public string Name { get; set; } = ""; public string Award { get; set; } = ""; public string Notes { get; set; } = ""; }
    private sealed class TruckRequest { public string Id { get; set; } = ""; public string Number { get; set; } = ""; public string Name { get; set; } = ""; public string Model { get; set; } = ""; public string Driver { get; set; } = ""; public string Plate { get; set; } = ""; public string Odometer { get; set; } = ""; public string Location { get; set; } = ""; public string Status { get; set; } = "Available"; public string Condition { get; set; } = ""; public string Fuel { get; set; } = ""; public string Notes { get; set; } = ""; }
    private sealed class GarageRequest { public string Id { get; set; } = ""; public string City { get; set; } = ""; public string CityName { get; set; } = ""; public string State { get; set; } = ""; public string Country { get; set; } = ""; public string Slots { get; set; } = ""; public string Size { get; set; } = "Small"; public int Capacity { get; set; } = 0; public bool Owned { get; set; } = true; public string Notes { get; set; } = ""; }
}
