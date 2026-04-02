using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class AwardRoutes
{
    private sealed class CreateAwardReq
    {
        public string? GuildId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? IconEmoji { get; set; }
        public bool IsAchievement { get; set; }
        public string? CreatedByUserId { get; set; }
        public string? CreatedByUsername { get; set; }
    }

    private sealed class AssignAwardReq
    {
        public string? GuildId { get; set; }
        public string? DriverId { get; set; }
        public string? DriverName { get; set; }
        public string? AwardId { get; set; }
        public string? AwardedByUserId { get; set; }
        public string? AwardedByUsername { get; set; }
        public string? Note { get; set; }
    }

    public static void Register(WebApplication app, BotServices services, JsonSerializerOptions json)
    {
        app.MapPost("/api/vtc/awards/create", async (HttpContext ctx) =>
        {
            if (services.AwardStore == null)
                return Results.Ok(new { ok = false, error = "award_store_not_ready" });

            var data = await ctx.Request.ReadFromJsonAsync<CreateAwardReq>(json);
            if (data == null)
                return Results.BadRequest(new { ok = false, error = "invalid_body" });

            var guildId = (data.GuildId ?? "").Trim();
            var name = (data.Name ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { ok = false, error = "guildId_and_name_required" });

            var award = new VtcAward
            {
                GuildId = guildId,
                Name = name,
                Description = (data.Description ?? "").Trim(),
                IconEmoji = string.IsNullOrWhiteSpace(data.IconEmoji) ? "🏆" : data.IconEmoji!.Trim(),
                CreatedByUserId = (data.CreatedByUserId ?? "").Trim(),
                CreatedByUsername = (data.CreatedByUsername ?? "").Trim(),
                IsAchievement = data.IsAchievement,
                CreatedUtc = DateTime.UtcNow
            };

            services.AwardStore.Add(award);
            return Results.Ok(new { ok = true, award });
        });

        app.MapGet("/api/vtc/awards", (string guildId) =>
        {
            if (services.AwardStore == null)
                return Results.Ok(new { ok = false, error = "award_store_not_ready" });

            var awards = services.AwardStore.GetAll(guildId ?? "");
            return Results.Ok(new { ok = true, awards });
        });

        app.MapPost("/api/vtc/awards/assign", async (HttpContext ctx) =>
        {
            if (services.AwardStore == null || services.DriverAwardStore == null)
                return Results.Ok(new { ok = false, error = "award_system_not_ready" });

            var data = await ctx.Request.ReadFromJsonAsync<AssignAwardReq>(json);
            if (data == null)
                return Results.BadRequest(new { ok = false, error = "invalid_body" });

            var guildId = (data.GuildId ?? "").Trim();
            var driverId = (data.DriverId ?? "").Trim();
            var awardId = (data.AwardId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId) ||
                string.IsNullOrWhiteSpace(driverId) ||
                string.IsNullOrWhiteSpace(awardId))
            {
                return Results.BadRequest(new { ok = false, error = "guildId_driverId_awardId_required" });
            }

            var award = services.AwardStore.GetById(guildId, awardId);
            if (award == null)
                return Results.NotFound(new { ok = false, error = "award_not_found" });

            var entry = new DriverAwardEntry
            {
                GuildId = guildId,
                DriverId = driverId,
                DriverName = (data.DriverName ?? "").Trim(),
                AwardId = awardId,
                AwardedByUserId = (data.AwardedByUserId ?? "").Trim(),
                AwardedByUsername = (data.AwardedByUsername ?? "").Trim(),
                Note = (data.Note ?? "").Trim(),
                AwardedUtc = DateTime.UtcNow
            };

            services.DriverAwardStore.Add(entry);

            return Results.Ok(new
            {
                ok = true,
                award,
                entry
            });
        });

        app.MapGet("/api/vtc/awards/driver", (string guildId, string driverId) =>
        {
            if (services.AwardStore == null || services.DriverAwardStore == null)
                return Results.Ok(new { ok = false, error = "award_system_not_ready" });

            var entries = services.DriverAwardStore.GetForDriver(guildId ?? "", driverId ?? "");
            var defs = services.AwardStore.GetAll(guildId ?? "").ToDictionary(x => x.Id, x => x);

            var awards = entries.Select(x =>
            {
                defs.TryGetValue(x.AwardId, out var def);

                return new
                {
                    x.DriverId,
                    x.DriverName,
                    x.AwardId,
                    x.AwardedUtc,
                    x.AwardedByUsername,
                    x.Note,
                    award = def
                };
            });

            return Results.Ok(new { ok = true, awards });
        });
    }
}
