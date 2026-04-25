using Microsoft.AspNetCore.Mvc;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class PortalDataRoutes
{
    public static WebApplication MapPortalDataRoutes(this WebApplication app)
    {
        app.MapGet("/api/vtc/portal/data", async (
            [FromQuery] string guildId,
            PortalDataStore store,
            HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);
            return Results.Ok(new { ok = true, data = guild });
        });

        app.MapGet("/api/vtc/portal/settings", async (
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);
            return Results.Ok(new { ok = true, data = guild });
        });

        app.MapPost("/api/vtc/portal/settings", async (
            [FromBody] PortalGuildData payload,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(payload.GuildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(payload.GuildId, g =>
            {
                g.SiteTitle = payload.SiteTitle ?? "";
                g.WelcomeText = payload.WelcomeText ?? "";
                g.CompanyInfo = payload.CompanyInfo ?? "";
                g.HeroImageUrl = payload.HeroImageUrl ?? "";
                g.JoinDiscordUrl = payload.JoinDiscordUrl ?? "";
                g.LearnMoreUrl = payload.LearnMoreUrl ?? "";
                g.LatestInfo = payload.LatestInfo ?? new();
                g.FeaturedDrivers = payload.FeaturedDrivers ?? new();
                g.SelectedFeaturedDriver = payload.SelectedFeaturedDriver ?? "";
                g.Drivers = payload.Drivers ?? new();
                g.Trucks = payload.Trucks ?? new();
                g.Garages = payload.Garages ?? new();
            });

            return Results.Ok(new { ok = true, data = updated });
        });

        app.MapGet("/api/vtc/portal/drivers", async (
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);
            return Results.Ok(new { ok = true, data = guild.Drivers });
        });

        app.MapPost("/api/vtc/portal/drivers", async (
            [FromQuery] string guildId,
            [FromBody] PortalDriver driver,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(guildId, g =>
            {
                if (string.IsNullOrWhiteSpace(driver.Id))
                    driver.Id = Guid.NewGuid().ToString("N");

                var existing = g.Drivers.FindIndex(x => x.Id == driver.Id);
                if (existing >= 0) g.Drivers[existing] = driver;
                else g.Drivers.Add(driver);
            });

            return Results.Ok(new { ok = true, data = updated.Drivers });
        });

        app.MapDelete("/api/vtc/portal/drivers/{id}", async (
            string id,
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(guildId, g =>
            {
                g.Drivers.RemoveAll(x => x.Id == id);
                g.FeaturedDrivers.RemoveAll(x => x.Id == id);
                if (g.SelectedFeaturedDriver == id) g.SelectedFeaturedDriver = "";
            });

            return Results.Ok(new { ok = true, data = updated.Drivers });
        });

        app.MapGet("/api/vtc/portal/fleet", async (
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);
            return Results.Ok(new { ok = true, data = guild.Trucks });
        });

        app.MapPost("/api/vtc/portal/fleet", async (
            [FromQuery] string guildId,
            [FromBody] PortalTruck truck,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(guildId, g =>
            {
                if (string.IsNullOrWhiteSpace(truck.Id))
                    truck.Id = Guid.NewGuid().ToString("N");

                var existing = g.Trucks.FindIndex(x => x.Id == truck.Id);
                if (existing >= 0) g.Trucks[existing] = truck;
                else g.Trucks.Add(truck);
            });

            return Results.Ok(new { ok = true, data = updated.Trucks });
        });

        app.MapDelete("/api/vtc/portal/fleet/{id}", async (
            string id,
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(guildId, g => g.Trucks.RemoveAll(x => x.Id == id));
            return Results.Ok(new { ok = true, data = updated.Trucks });
        });

        app.MapGet("/api/vtc/portal/garages", async (
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);
            return Results.Ok(new { ok = true, data = guild.Garages });
        });

        app.MapPost("/api/vtc/portal/garages", async (
            [FromQuery] string guildId,
            [FromBody] PortalGarage garage,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(guildId, g =>
            {
                if (string.IsNullOrWhiteSpace(garage.Id))
                    garage.Id = Guid.NewGuid().ToString("N");

                if (string.IsNullOrWhiteSpace(garage.PurchasedUtc))
                    garage.PurchasedUtc = DateTimeOffset.UtcNow.ToString("O");

                var existing = g.Garages.FindIndex(x => x.Id == garage.Id);
                if (existing >= 0) g.Garages[existing] = garage;
                else g.Garages.Add(garage);
            });

            return Results.Ok(new { ok = true, data = updated.Garages });
        });

        app.MapDelete("/api/vtc/portal/garages/{id}", async (
            string id,
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var updated = store.UpdateGuild(guildId, g => g.Garages.RemoveAll(x => x.Id == id));
            return Results.Ok(new { ok = true, data = updated.Garages });
        });

        return app;
    }
}
