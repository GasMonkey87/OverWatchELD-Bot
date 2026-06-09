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

            foreach (var saved in root.Guilds.Values.Where(g => g.IsPublicDirectoryListed))
                byGuild[saved.GuildId] = saved;

            foreach (var guild in discord.Guilds)
            {
                var id = guild.Id.ToString();
                if (!byGuild.ContainsKey(id))
                {
                    byGuild[id] = new PortalGuildData
                    {
                        GuildId = id,
                        CompanyName = guild.Name,
                        WelcomeText = $"{guild.Name} is registered with OverWatch ELD.",
                        LogoImageUrl = guild.IconUrl ?? "",
                        IsPublicDirectoryListed = true,
                        IsAcceptingApplications = true
                    };
                }
            }

            var vtcs = byGuild.Values
                .Select(g => BuildPublicCard(g, discord))
                .OrderBy(x => x.name)
                .ToList();

            return Results.Json(new { ok = true, vtcs, count = vtcs.Count, botGuildCount = discord.Guilds.Count });
        });

        app.MapGet("/api/vtc/public/{guildId}", (string guildId, PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var root = portalStore.Load();
            root.Guilds.TryGetValue(guildId, out var portal);
            var guild = ulong.TryParse(guildId, out var parsed) ? discord.GetGuild(parsed) : null;

            if (portal == null && guild == null)
                return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);

            portal ??= new PortalGuildData
            {
                GuildId = guildId,
                CompanyName = guild?.Name ?? "Registered VTC",
                WelcomeText = $"{guild?.Name ?? "This VTC"} is registered with OverWatch ELD.",
                LogoImageUrl = guild?.IconUrl ?? "",
                IsPublicDirectoryListed = true,
                IsAcceptingApplications = true
            };

            if (!portal.IsPublicDirectoryListed)
                return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);

            var questions = portal.ApplicationQuestions
                .Where(q => !string.IsNullOrWhiteSpace(q.Question))
                .Select(q => new { id = q.Id, question = q.Question, type = q.Type, required = q.Required })
                .ToList();

            return Results.Json(new
            {
                ok = true,
                vtc = BuildPublicCard(portal, discord),
                profile = new
                {
                    guildId,
                    name = FirstNonBlank(portal.CompanyName, portal.SiteTitle, guild?.Name, "Registered VTC"),
                    description = FirstNonBlank(portal.PublicRecruitingMessage, portal.WelcomeText, portal.CompanyInfo, "This VTC is registered with OverWatch ELD."),
                    about = FirstNonBlank(portal.CompanyInfo, portal.WelcomeText, "No public description has been added yet."),
                    requirements = portal.PublicRequirements,
                    logoUrl = FirstNonBlank(portal.LogoImageUrl, guild?.IconUrl, ""),
                    bannerUrl = FirstNonBlank(portal.BannerImageUrl, portal.HeroImageUrl, ""),
                    acceptingApplications = portal.IsAcceptingApplications,
                    applicationStatusText = portal.IsAcceptingApplications ? "Accepting Applications" : "Applications Closed",
                    memberCount = guild?.Users.Count(u => !u.IsBot) ?? portal.Drivers.Count,
                    truckCount = portal.Trucks.Count,
                    garageCount = portal.Garages.Count,
                    questions
                }
            });
        });

        app.MapPost("/api/vtc/public/{guildId}/apply", async (string guildId, HttpContext ctx, PortalDataStore portalStore, DiscordSocketClient discord) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<SubmitApplicationRequest>() ?? new SubmitApplicationRequest();
            var root = portalStore.Load();
            root.Guilds.TryGetValue(guildId, out var portal);
            var guild = ulong.TryParse(guildId, out var parsed) ? discord.GetGuild(parsed) : null;

            if (portal == null && guild == null)
                return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);

            portal ??= new PortalGuildData
            {
                GuildId = guildId,
                CompanyName = guild?.Name ?? "Registered VTC",
                WelcomeText = $"{guild?.Name ?? "This VTC"} is registered with OverWatch ELD.",
                LogoImageUrl = guild?.IconUrl ?? "",
                IsPublicDirectoryListed = true,
                IsAcceptingApplications = true
            };

            if (!portal.IsPublicDirectoryListed)
                return Results.Json(new { ok = false, error = "NotFound" }, statusCode: 404);

            if (!portal.IsAcceptingApplications)
                return Results.Json(new { ok = false, error = "ApplicationsClosed" }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(req.ApplicantName) || string.IsNullOrWhiteSpace(req.ApplicantDiscord))
                return Results.Json(new { ok = false, error = "Name and Discord are required." }, statusCode: 400);

            var answers = new List<PortalApplicationAnswer>();
            foreach (var q in portal.ApplicationQuestions.Where(q => !string.IsNullOrWhiteSpace(q.Question)))
            {
                req.Answers.TryGetValue(q.Id, out var answer);
                answer ??= "";
                if (q.Required && string.IsNullOrWhiteSpace(answer))
                    return Results.Json(new { ok = false, error = $"Missing required answer: {q.Question}" }, statusCode: 400);

                answers.Add(new PortalApplicationAnswer
                {
                    QuestionId = q.Id,
                    Question = q.Question,
                    Answer = answer.Trim()
                });
            }

            var appRow = new PortalApplication
            {
                ApplicantName = req.ApplicantName.Trim(),
                ApplicantEmail = req.ApplicantEmail.Trim(),
                ApplicantDiscord = req.ApplicantDiscord.Trim(),
                ApplicantDiscordUserId = req.ApplicantDiscordUserId.Trim(),
                Answers = answers,
                Status = "Pending",
                SubmittedUtc = DateTimeOffset.UtcNow
            };

            portalStore.UpdateGuild(guildId, g =>
            {
                g.CompanyName = FirstNonBlank(g.CompanyName, portal.CompanyName);
                g.WelcomeText = FirstNonBlank(g.WelcomeText, portal.WelcomeText);
                g.LogoImageUrl = FirstNonBlank(g.LogoImageUrl, portal.LogoImageUrl);
                g.Applications.Add(appRow);
            });

            return Results.Json(new { ok = true, applicationId = appRow.Id });
        });

        app.MapPost("/api/vtc/admin/{guildId}/settings", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);

            var req = await ctx.Request.ReadFromJsonAsync<SaveVtcSettingsRequest>() ?? new SaveVtcSettingsRequest();
            var updated = portalStore.UpdateGuild(guildId, g =>
            {
                g.IsAcceptingApplications = req.IsAcceptingApplications;
                g.IsPublicDirectoryListed = req.IsPublicDirectoryListed;
                if (req.PublicRecruitingMessage != null)
                    g.PublicRecruitingMessage = req.PublicRecruitingMessage.Trim();
                if (req.PublicRequirements != null)
                    g.PublicRequirements = req.PublicRequirements.Trim();
            });

            return Results.Json(new
            {
                ok = true,
                isAcceptingApplications = updated.IsAcceptingApplications,
                isPublicDirectoryListed = updated.IsPublicDirectoryListed,
                applicationStatusText = updated.IsAcceptingApplications ? "Accepting Applications" : "Applications Closed"
            });
        });

        app.MapPost("/api/vtc/admin/{guildId}/questions", async (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);

            var req = await ctx.Request.ReadFromJsonAsync<SaveQuestionsRequest>() ?? new SaveQuestionsRequest();
            var questions = req.Questions
                .Where(q => !string.IsNullOrWhiteSpace(q.Question))
                .Select(q => new PortalApplicationQuestion
                {
                    Id = string.IsNullOrWhiteSpace(q.Id) ? Guid.NewGuid().ToString("N") : q.Id,
                    Question = q.Question.Trim(),
                    Type = FirstNonBlank(q.Type, "textarea"),
                    Required = q.Required
                })
                .ToList();

            portalStore.UpdateGuild(guildId, g => g.ApplicationQuestions = questions);
            return Results.Json(new { ok = true, questions });
        });

        app.MapGet("/api/vtc/admin/{guildId}/applications", (string guildId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);

            var portal = portalStore.GetGuild(guildId);
            var applications = portal.Applications
                .OrderByDescending(a => a.SubmittedUtc)
                .Select(a => new
                {
                    a.Id,
                    a.ApplicantName,
                    a.ApplicantEmail,
                    a.ApplicantDiscord,
                    a.Status,
                    a.SubmittedUtc,
                    a.ReviewedUtc,
                    a.ReviewedBy,
                    a.ReviewNotes,
                    answers = a.Answers.Select(x => new { x.QuestionId, x.Question, x.Answer })
                })
                .ToList();

            return Results.Json(new { ok = true, applications });
        });

        app.MapPost("/api/vtc/admin/{guildId}/applications/{applicationId}/review", async (string guildId, string applicationId, HttpContext ctx, PortalDataStore portalStore, WebSessionStore sessions, DiscordSocketClient discord) =>
        {
            var access = CheckManagerAccess(ctx, sessions, discord, guildId);
            if (!access.Ok) return Results.Json(new { ok = false, error = access.Error }, statusCode: access.StatusCode);

            var req = await ctx.Request.ReadFromJsonAsync<ReviewApplicationRequest>() ?? new ReviewApplicationRequest();
            var status = string.Equals(req.Status, "Approved", StringComparison.OrdinalIgnoreCase) ? "Approved" : "Denied";
            var updated = false;

            portalStore.UpdateGuild(guildId, g =>
            {
                var appRow = g.Applications.FirstOrDefault(a => a.Id == applicationId);
                if (appRow == null) return;
                appRow.Status = status;
                appRow.ReviewNotes = req.Notes.Trim();
                appRow.ReviewedBy = access.DisplayName;
                appRow.ReviewedUtc = DateTimeOffset.UtcNow;
                updated = true;
            });

            return updated
                ? Results.Json(new { ok = true, status })
                : Results.Json(new { ok = false, error = "ApplicationNotFound" }, statusCode: 404);
        });
    }

    private static dynamic BuildPublicCard(PortalGuildData portal, DiscordSocketClient discord)
    {
        var guild = ulong.TryParse(portal.GuildId, out var parsed) ? discord.GetGuild(parsed) : null;
        var name = FirstNonBlank(portal.CompanyName, portal.SiteTitle, guild?.Name, "Registered VTC");
        return new
        {
            guildId = portal.GuildId,
            name,
            description = FirstNonBlank(portal.PublicRecruitingMessage, portal.WelcomeText, portal.CompanyInfo, "Registered OverWatch ELD VTC"),
            logoUrl = FirstNonBlank(portal.LogoImageUrl, guild?.IconUrl, ""),
            bannerUrl = FirstNonBlank(portal.BannerImageUrl, portal.HeroImageUrl, ""),
            acceptingApplications = portal.IsAcceptingApplications,
            applicationStatusText = portal.IsAcceptingApplications ? "Accepting Applications" : "Applications Closed",
            memberCount = guild?.Users.Count(u => !u.IsBot) ?? portal.Drivers.Count,
            truckCount = portal.Trucks.Count,
            garageCount = portal.Garages.Count,
            updatedUtc = portal.UpdatedUtc
        };
    }

    private static AccessResult CheckManagerAccess(HttpContext ctx, WebSessionStore sessions, DiscordSocketClient discord, string guildId)
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

        if (string.IsNullOrWhiteSpace(sessionId) || !sessions.TryGet(sessionId, out var session) || session == null)
            return new AccessResult(false, "NotAuthenticated", 401, "");

        if (!ulong.TryParse(guildId, out var parsedGuildId) || !ulong.TryParse(session.DiscordUserId, out var parsedUserId))
            return new AccessResult(false, "Forbidden", 403, "");

        var guild = discord.GetGuild(parsedGuildId);
        var user = guild?.GetUser(parsedUserId);
        if (guild == null || user == null)
            return new AccessResult(false, "Forbidden", 403, "");

        var role = ResolveMemberRole(guild, user);
        if (role is not "Owner" and not "Admin" and not "Manager")
            return new AccessResult(false, "Forbidden", 403, "");

        return new AccessResult(true, "", 200, FirstNonBlank(user.DisplayName, user.GlobalName, user.Username));
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

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    private sealed record AccessResult(bool Ok, string Error, int StatusCode, string DisplayName);

    private sealed class SubmitApplicationRequest
    {
        public string ApplicantName { get; set; } = "";
        public string ApplicantEmail { get; set; } = "";
        public string ApplicantDiscord { get; set; } = "";
        public string ApplicantDiscordUserId { get; set; } = "";
        public Dictionary<string, string> Answers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SaveVtcSettingsRequest
    {
        public bool IsAcceptingApplications { get; set; } = true;
        public bool IsPublicDirectoryListed { get; set; } = true;
        public string? PublicRecruitingMessage { get; set; }
        public string? PublicRequirements { get; set; }
    }

    private sealed class SaveQuestionsRequest
    {
        public List<QuestionDto> Questions { get; set; } = new();
    }

    private sealed class QuestionDto
    {
        public string Id { get; set; } = "";
        public string Question { get; set; } = "";
        public string Type { get; set; } = "textarea";
        public bool Required { get; set; } = true;
    }

    private sealed class ReviewApplicationRequest
    {
        public string Status { get; set; } = "Denied";
        public string Notes { get; set; } = "";
    }
}
