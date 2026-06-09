using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcDirectoryRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/vtc/directory", (PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var root = portalStore.Load();
            var byGuild = new Dictionary<string, PortalGuildData>(StringComparer.Ordinal);
            foreach (var saved in root.Guilds.Values.Where(g => g.IsPublicDirectoryListed)) byGuild[saved.GuildId] = saved;
            foreach (var guild in discord.Guilds)
            {
                var id = guild.Id.ToString();
                if (!byGuild.ContainsKey(id)) byGuild[id] = new PortalGuildData { GuildId = id, CompanyName = guild.Name, WelcomeText = $"{guild.Name} is registered with OverWatch ELD.", LogoImageUrl = guild.IconUrl ?? "", IsPublicDirectoryListed = true, IsAcceptingApplications = true };
            }
            var vtcs = byGuild.Values.Select(g => BuildPublicCard(g, discord)).OrderBy(x => x.name).ToList();
            return Results.Json(new { ok = true, vtcs, count = vtcs.Count, botGuildCount = discord.Guilds.Count });
        });

        app.MapGet("/api/vtc/public/{guildId}", (string guildId, PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var root = portalStore.Load(); root.Guilds.TryGetValue(guildId, out var portal);
            var guild = ulong.TryParse(guildId, out var parsed) ? discord.GetGuild(parsed) : null;
            if (portal == null && guild == null) return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);
            portal ??= new PortalGuildData { GuildId = guildId, CompanyName = guild?.Name ?? "Registered VTC", WelcomeText = $"{guild?.Name ?? "This VTC"} is registered with OverWatch ELD.", LogoImageUrl = guild?.IconUrl ?? "", IsPublicDirectoryListed = true, IsAcceptingApplications = true };
            if (!portal.IsPublicDirectoryListed) return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);
            var questions = portal.ApplicationQuestions.Where(q => !string.IsNullOrWhiteSpace(q.Question)).Select(q => new { id = q.Id, question = q.Question, type = q.Type, required = q.Required }).ToList();
            return Results.Json(new { ok = true, vtc = BuildPublicCard(portal, discord), profile = new { guildId, name = FirstNonBlank(portal.CompanyName, portal.SiteTitle, guild?.Name, "Registered VTC"), description = FirstNonBlank(portal.PublicRecruitingMessage, portal.WelcomeText, portal.CompanyInfo, "This VTC is registered with OverWatch ELD."), about = FirstNonBlank(portal.CompanyInfo, portal.WelcomeText, "No public description has been added yet."), requirements = portal.PublicRequirements, logoUrl = FirstNonBlank(portal.LogoImageUrl, guild?.IconUrl, ""), bannerUrl = FirstNonBlank(portal.BannerImageUrl, portal.HeroImageUrl, ""), acceptingApplications = portal.IsAcceptingApplications, applicationStatusText = portal.IsAcceptingApplications ? "Accepting Applications" : "Applications Closed", memberCount = guild?.Users.Count(u => !u.IsBot) ?? portal.Drivers.Count, truckCount = portal.Trucks.Count, garageCount = portal.Garages.Count, questions } });
        });

        app.MapPost("/api/vtc/public/{guildId}/apply", async (string guildId, HttpContext ctx, PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<SubmitApplicationRequest>() ?? new SubmitApplicationRequest();
            var root = portalStore.Load(); root.Guilds.TryGetValue(guildId, out var portal);
            var guild = ulong.TryParse(guildId, out var parsed) ? discord.GetGuild(parsed) : null;
            if (portal == null && guild == null) return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);
            portal ??= new PortalGuildData { GuildId = guildId, CompanyName = guild?.Name ?? "Registered VTC", WelcomeText = $"{guild?.Name ?? "This VTC"} is registered with OverWatch ELD.", LogoImageUrl = guild?.IconUrl ?? "", IsPublicDirectoryListed = true, IsAcceptingApplications = true };
            if (!portal.IsPublicDirectoryListed) return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);
            if (!portal.IsAcceptingApplications) return Results.Json(new { ok = false, error = "ApplicationsClosed" }, statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.ApplicantName) || string.IsNullOrWhiteSpace(req.ApplicantDiscord)) return Results.Json(new { ok = false, error = "Name and Discord are required." }, statusCode: 400);
            var answers = new List<PortalApplicationAnswer>();
            foreach (var q in portal.ApplicationQuestions.Where(q => !string.IsNullOrWhiteSpace(q.Question)))
            {
                req.Answers.TryGetValue(q.Id, out var answer); answer ??= "";
                if (q.Required && string.IsNullOrWhiteSpace(answer)) return Results.Json(new { ok = false, error = $"Missing required answer: {q.Question}" }, statusCode: 400);
                answers.Add(new PortalApplicationAnswer { QuestionId = q.Id, Question = q.Question, Answer = answer.Trim() });
            }
            var appRow = new PortalApplication { ApplicantName = req.ApplicantName.Trim(), ApplicantEmail = req.ApplicantEmail.Trim(), ApplicantDiscord = req.ApplicantDiscord.Trim(), ApplicantDiscordUserId = req.ApplicantDiscordUserId.Trim(), Answers = answers, Status = "Pending", SubmittedUtc = DateTimeOffset.UtcNow };
            portalStore.UpdateGuild(guildId, g => { g.CompanyName = FirstNonBlank(g.CompanyName, portal.CompanyName); g.WelcomeText = FirstNonBlank(g.WelcomeText, portal.WelcomeText); g.LogoImageUrl = FirstNonBlank(g.LogoImageUrl, portal.LogoImageUrl); g.Applications.Add(appRow); AddAudit(g, "Application Submitted", appRow.ApplicantName + " submitted an application."); });
            return Results.Json(new { ok = true, applicationId = appRow.Id });
        });

        app.MapPost("/api/vtc/admin/{guildId}/settings", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<SaveVtcSettingsRequest>() ?? new SaveVtcSettingsRequest();
            var updated = portalStore.UpdateGuild(guildId, g => { g.IsAcceptingApplications = req.IsAcceptingApplications; g.IsPublicDirectoryListed = req.IsPublicDirectoryListed; if (req.PublicRecruitingMessage != null) g.PublicRecruitingMessage = req.PublicRecruitingMessage.Trim(); if (req.PublicRequirements != null) g.PublicRequirements = req.PublicRequirements.Trim(); AddAudit(g, "Application Settings Saved", "Updated by " + access.DisplayName); });
            return Results.Json(new { ok = true, isAcceptingApplications = updated.IsAcceptingApplications, isPublicDirectoryListed = updated.IsPublicDirectoryListed, applicationStatusText = updated.IsAcceptingApplications ? "Accepting Applications" : "Applications Closed" });
        });

        app.MapPost("/api/vtc/admin/{guildId}/questions", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<SaveQuestionsRequest>() ?? new SaveQuestionsRequest();
            var questions = req.Questions.Where(q => !string.IsNullOrWhiteSpace(q.Question)).Select(q => new PortalApplicationQuestion { Id = string.IsNullOrWhiteSpace(q.Id) ? Guid.NewGuid().ToString("N") : q.Id, Question = q.Question.Trim(), Type = FirstNonBlank(q.Type, "textarea"), Required = q.Required }).ToList();
            portalStore.UpdateGuild(guildId, g => { g.ApplicationQuestions = questions; AddAudit(g, "Application Questions Saved", questions.Count + " questions saved by " + access.DisplayName); });
            return Results.Json(new { ok = true, questions });
        });

        app.MapGet("/api/vtc/admin/{guildId}/applications", (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var applications = portalStore.GetGuild(guildId).Applications.OrderByDescending(a => a.SubmittedUtc).Select(a => new { a.Id, a.ApplicantName, a.ApplicantEmail, a.ApplicantDiscord, a.Status, a.SubmittedUtc, a.ReviewedUtc, a.ReviewedBy, a.ReviewNotes, answers = a.Answers.Select(x => new { x.QuestionId, x.Question, x.Answer }) }).ToList();
            return Results.Json(new { ok = true, applications });
        });

        app.MapPost("/api/vtc/admin/{guildId}/applications/{applicationId}/review", async (string guildId, string applicationId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<ReviewApplicationRequest>() ?? new ReviewApplicationRequest();
            var status = req.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase) ? "Approved" : req.Status.Equals("Interview", StringComparison.OrdinalIgnoreCase) ? "Interview" : "Denied";
            var updated = false; PortalDriver? addedDriver = null;
            portalStore.UpdateGuild(guildId, g =>
            {
                var appRow = g.Applications.FirstOrDefault(a => a.Id == applicationId); if (appRow == null) return;
                appRow.Status = status; appRow.ReviewNotes = req.Notes.Trim(); appRow.ReviewedBy = access.DisplayName; appRow.ReviewedUtc = DateTimeOffset.UtcNow; updated = true;
                if (status == "Approved")
                {
                    addedDriver = FindDriver(g, appRow.ApplicantDiscordUserId, appRow.ApplicantName, true);
                    addedDriver!.Name = FirstNonBlank(appRow.ApplicantName, addedDriver.Name, "Driver"); addedDriver.DiscordUsername = FirstNonBlank(appRow.ApplicantDiscord, addedDriver.DiscordUsername); addedDriver.DiscordUserId = FirstNonBlank(appRow.ApplicantDiscordUserId, addedDriver.DiscordUserId); addedDriver.Role = FirstNonBlank(addedDriver.Role, "Driver"); addedDriver.Status = "Approved"; addedDriver.YearsInVtc = FirstNonBlank(addedDriver.YearsInVtc, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
                }
                AddAudit(g, "Application " + status, appRow.ApplicantName + " reviewed by " + access.DisplayName);
            });
            return updated ? Results.Json(new { ok = true, status, driver = addedDriver }) : Results.Json(new { ok = false, error = "ApplicationNotFound" }, statusCode: 404);
        });

        app.MapPost("/api/vtc/admin/{guildId}/roster/role", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<RosterRoleRequest>() ?? new RosterRoleRequest(); PortalDriver? driver = null;
            portalStore.UpdateGuild(guildId, g => { driver = FindDriver(g, req.DiscordUserId, req.DriverName, true); driver!.Name = FirstNonBlank(req.DriverName, driver.Name, "Driver"); driver.DiscordUserId = FirstNonBlank(req.DiscordUserId, driver.DiscordUserId); driver.Role = FirstNonBlank(req.Role, driver.Role, "Driver"); AddAudit(g, "Role Updated", driver.Name + " set to " + driver.Role + " by " + access.DisplayName); });
            return Results.Json(new { ok = true, driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/roster/truck", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<AssignTruckRequest>() ?? new AssignTruckRequest(); PortalDriver? driver = null; PortalTruck? truck = null;
            portalStore.UpdateGuild(guildId, g => { driver = FindDriver(g, req.DiscordUserId, req.DriverName, true); driver!.AssignedTruck = FirstNonBlank(req.TruckName, req.TruckNumber, driver.AssignedTruck); truck = FindTruck(g, req.TruckId, req.TruckNumber, req.TruckName, true); truck!.Driver = driver.Name; truck.DriverDiscordUserId = driver.DiscordUserId; truck.Status = "Assigned"; AddAudit(g, "Truck Assigned", driver.Name + " assigned to " + FirstNonBlank(truck.TruckNumber, truck.Name)); });
            return Results.Json(new { ok = true, driver, truck });
        });

        app.MapPost("/api/vtc/admin/{guildId}/awards", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<AwardRequest>() ?? new AwardRequest(); PortalDriver? driver = null;
            portalStore.UpdateGuild(guildId, g => { driver = FindDriver(g, req.DiscordUserId, req.DriverName, true); driver!.Achievement = FirstNonBlank(req.AwardName, driver.Achievement); driver.Bio = FirstNonBlank(req.Notes, driver.Bio); if (!g.FeaturedDrivers.Any(d => d.Id == driver.Id || Same(d.DiscordUserId, driver.DiscordUserId) || Same(d.Name, driver.Name))) g.FeaturedDrivers.Add(driver); AddAudit(g, "Award Assigned", driver.Name + " received " + driver.Achievement); });
            return Results.Json(new { ok = true, award = driver });
        });

        app.MapPost("/api/vtc/admin/{guildId}/trucks", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<TruckRequest>() ?? new TruckRequest(); PortalTruck? truck = null;
            portalStore.UpdateGuild(guildId, g => { truck = FindTruck(g, req.Id, req.TruckNumber, req.Name, true); truck!.TruckNumber = FirstNonBlank(req.TruckNumber, truck.TruckNumber); truck.Name = FirstNonBlank(req.Name, truck.Name); truck.Model = FirstNonBlank(req.Model, truck.Model, req.Name); truck.Driver = FirstNonBlank(req.Driver, truck.Driver); truck.Plate = FirstNonBlank(req.Plate, truck.Plate); truck.Odometer = FirstNonBlank(req.Odometer, truck.Odometer); truck.Location = FirstNonBlank(req.Location, truck.Location); truck.Status = FirstNonBlank(req.Status, truck.Status, "Available"); truck.Condition = FirstNonBlank(req.Condition, truck.Condition); truck.Fuel = FirstNonBlank(req.Fuel, truck.Fuel); truck.Notes = FirstNonBlank(req.Notes, truck.Notes); AddAudit(g, "Truck Saved", FirstNonBlank(truck.TruckNumber, truck.Name, truck.Model)); });
            return Results.Json(new { ok = true, truck });
        });

        app.MapPost("/api/vtc/admin/{guildId}/garages", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var req = await ctx.Request.ReadFromJsonAsync<GarageRequest>() ?? new GarageRequest(); PortalGarage? garage = null;
            portalStore.UpdateGuild(guildId, g => { garage = string.IsNullOrWhiteSpace(req.Id) ? null : g.Garages.FirstOrDefault(x => x.Id == req.Id); if (garage == null) { garage = new PortalGarage(); g.Garages.Add(garage); } garage.City = FirstNonBlank(req.City, garage.City); garage.CityName = FirstNonBlank(req.CityName, garage.CityName, req.City); garage.State = FirstNonBlank(req.State, garage.State); garage.Country = FirstNonBlank(req.Country, garage.Country, "USA"); garage.Slots = FirstNonBlank(req.Slots, garage.Slots); garage.Size = FirstNonBlank(req.Size, garage.Size, "Small"); garage.TruckCapacity = req.TruckCapacity > 0 ? req.TruckCapacity : garage.TruckCapacity; garage.IsOwned = req.IsOwned || garage.IsOwned; garage.Notes = FirstNonBlank(req.Notes, garage.Notes); AddAudit(g, "Garage Saved", FirstNonBlank(garage.CityName, garage.City, "Garage")); });
            return Results.Json(new { ok = true, garage });
        });

        app.MapGet("/api/vtc/admin/{guildId}/audit", (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId); if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);
            var audit = portalStore.GetGuild(guildId).LatestInfo.OrderByDescending(x => x.CreatedUtc).Take(100).Select(x => new { x.Id, title = x.Title, body = x.Body, meta = x.Meta, createdUtc = x.CreatedUtc }).ToList();
            return Results.Json(new { ok = true, audit });
        });
    }

    private static dynamic BuildPublicCard(PortalGuildData portal, DiscordSocketClient discord)
    {
        var guild = ulong.TryParse(portal.GuildId, out var parsed) ? discord.GetGuild(parsed) : null; var name = FirstNonBlank(portal.CompanyName, portal.SiteTitle, guild?.Name, "Registered VTC");
        return new { guildId = portal.GuildId, name, description = FirstNonBlank(portal.PublicRecruitingMessage, portal.WelcomeText, portal.CompanyInfo, "Registered OverWatch ELD VTC"), logoUrl = FirstNonBlank(portal.LogoImageUrl, guild?.IconUrl, ""), bannerUrl = FirstNonBlank(portal.BannerImageUrl, portal.HeroImageUrl, ""), acceptingApplications = portal.IsAcceptingApplications, applicationStatusText = portal.IsAcceptingApplications ? "Accepting Applications" : "Applications Closed", memberCount = guild?.Users.Count(u => !u.IsBot) ?? portal.Drivers.Count, truckCount = portal.Trucks.Count, garageCount = portal.Garages.Count, updatedUtc = portal.UpdatedUtc };
    }

    private static PortalDriver FindDriver(PortalGuildData g, string discordId, string name, bool create) { var d = g.Drivers.FirstOrDefault(x => Same(x.DiscordUserId, discordId) || Same(x.Name, name) || Same(x.DiscordUsername, name)); if (d == null && create) { d = new PortalDriver { Name = FirstNonBlank(name, "Driver"), DiscordUserId = discordId ?? "", Role = "Driver", Status = "Member" }; g.Drivers.Add(d); } return d!; }
    private static PortalTruck FindTruck(PortalGuildData g, string id, string number, string name, bool create) { var t = g.Trucks.FirstOrDefault(x => Same(x.Id, id) || Same(x.TruckNumber, number) || Same(x.Name, name)); if (t == null && create) { t = new PortalTruck { TruckNumber = number ?? "", Name = name ?? "", Status = "Available" }; g.Trucks.Add(t); } return t!; }
    private static void AddAudit(PortalGuildData g, string title, string body) { g.LatestInfo.Insert(0, new PortalLatestInfo { Title = title, Body = body, Meta = "Admin", CreatedUtc = DateTimeOffset.UtcNow }); if (g.LatestInfo.Count > 250) g.LatestInfo = g.LatestInfo.Take(250).ToList(); }
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static AccessResult CheckManagerAccess(HttpContext ctx, WebSessionStore sessions, DiscordSocketClient discord, string guildId)
    {
        var sessionId = ctx.Request.Cookies["ow_session"]; if (string.IsNullOrWhiteSpace(sessionId)) { var auth = ctx.Request.Headers.Authorization.ToString(); if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessionId = auth[7..].Trim(); } if (string.IsNullOrWhiteSpace(sessionId)) sessionId = ctx.Request.Headers["X-OverWatch-Session"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId) || !sessions.TryGet(sessionId, out var session) || session == null) return new AccessResult(false, "NotAuthenticated", 401, ""); if (!ulong.TryParse(guildId, out var parsedGuildId) || !ulong.TryParse(session.DiscordUserId, out var parsedUserId)) return new AccessResult(false, "Forbidden", 403, ""); var guild = discord.GetGuild(parsedGuildId); var user = guild?.GetUser(parsedUserId); if (guild == null || user == null) return new AccessResult(false, "Forbidden", 403, ""); var role = ResolveMemberRole(guild, user); if (role is not "Owner" and not "Admin" and not "Manager") return new AccessResult(false, "Forbidden", 403, ""); return new AccessResult(true, "", 200, FirstNonBlank(user.DisplayName, user.GlobalName, user.Username));
    }
    private static string ResolveMemberRole(SocketGuild guild, SocketGuildUser user) { if (guild.OwnerId == user.Id) return "Owner"; if (user.GuildPermissions.Administrator) return "Admin"; var roles = user.Roles.Select(r => r.Name.ToLowerInvariant()).ToList(); if (roles.Any(r => r.Contains("owner"))) return "Owner"; if (roles.Any(r => r.Contains("admin"))) return "Admin"; if (roles.Any(r => r.Contains("manager") || r.Contains("management"))) return "Manager"; return "Driver"; }
    private static string FirstNonBlank(params string?[] values) { foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value.Trim(); return ""; }
    private sealed record AccessResult(bool Ok, string Error, int StatusCode, string DisplayName);
    private sealed class SubmitApplicationRequest { public string ApplicantName { get; set; } = ""; public string ApplicantEmail { get; set; } = ""; public string ApplicantDiscord { get; set; } = ""; public string ApplicantDiscordUserId { get; set; } = ""; public Dictionary<string, string> Answers { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
    private sealed class SaveVtcSettingsRequest { public bool IsAcceptingApplications { get; set; } = true; public bool IsPublicDirectoryListed { get; set; } = true; public string? PublicRecruitingMessage { get; set; } public string? PublicRequirements { get; set; } }
    private sealed class SaveQuestionsRequest { public List<QuestionDto> Questions { get; set; } = new(); }
    private sealed class QuestionDto { public string Id { get; set; } = ""; public string Question { get; set; } = ""; public string Type { get; set; } = "textarea"; public bool Required { get; set; } = true; }
    private sealed class ReviewApplicationRequest { public string Status { get; set; } = "Denied"; public string Notes { get; set; } = ""; }
    private sealed class RosterRoleRequest { public string DiscordUserId { get; set; } = ""; public string DriverName { get; set; } = ""; public string Role { get; set; } = "Driver"; }
    private sealed class AssignTruckRequest { public string DiscordUserId { get; set; } = ""; public string DriverName { get; set; } = ""; public string TruckId { get; set; } = ""; public string TruckNumber { get; set; } = ""; public string TruckName { get; set; } = ""; }
    private sealed class AwardRequest { public string DiscordUserId { get; set; } = ""; public string DriverName { get; set; } = ""; public string AwardName { get; set; } = ""; public string Notes { get; set; } = ""; }
    private sealed class TruckRequest { public string Id { get; set; } = ""; public string TruckNumber { get; set; } = ""; public string Name { get; set; } = ""; public string Model { get; set; } = ""; public string Driver { get; set; } = ""; public string Plate { get; set; } = ""; public string Odometer { get; set; } = ""; public string Location { get; set; } = ""; public string Status { get; set; } = "Available"; public string Condition { get; set; } = ""; public string Fuel { get; set; } = ""; public string Notes { get; set; } = ""; }
    private sealed class GarageRequest { public string Id { get; set; } = ""; public string City { get; set; } = ""; public string CityName { get; set; } = ""; public string State { get; set; } = ""; public string Country { get; set; } = ""; public string Slots { get; set; } = ""; public string Size { get; set; } = "Small"; public int TruckCapacity { get; set; } = 0; public bool IsOwned { get; set; } = true; public string Notes { get; set; } = ""; }
}
