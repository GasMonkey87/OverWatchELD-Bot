using Discord.WebSocket;
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
            DiscordSocketClient discord,
            HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);

            // Pull default VTC name/logo from Discord when admins have not customized them yet.
            try
            {
                if (ulong.TryParse(guildId, out var parsedGuildId))
                {
                    var discordGuild = discord.GetGuild(parsedGuildId);
                    if (discordGuild != null)
                    {
                        var discordName = discordGuild.Name ?? "";
                        var discordLogo = discordGuild.IconUrl ?? "";

                        if (string.IsNullOrWhiteSpace(guild.CompanyName))
                            guild.CompanyName = discordName;

                        if (string.IsNullOrWhiteSpace(guild.SiteTitle))
                            guild.SiteTitle = discordName;

                        if (string.IsNullOrWhiteSpace(guild.LogoImageUrl))
                            guild.LogoImageUrl = discordLogo;
                    }
                }
            }
            catch { }

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
                g.CompanyName = payload.CompanyName ?? "";
                g.LogoImageUrl = payload.LogoImageUrl ?? "";
                g.CompanyPictureUrl = payload.CompanyPictureUrl ?? "";
                g.BannerImageUrl = payload.BannerImageUrl ?? "";
                g.WelcomeText = payload.WelcomeText ?? "";
                g.CompanyInfo = payload.CompanyInfo ?? "";
                g.HeroImageUrl = payload.HeroImageUrl ?? "";
                g.JoinDiscordUrl = payload.JoinDiscordUrl ?? "";
                g.LearnMoreUrl = payload.LearnMoreUrl ?? "";
                g.LatestInfo = payload.LatestInfo ?? new();
                g.FeaturedDrivers = payload.FeaturedDrivers ?? new();
                g.SlideshowImages = payload.SlideshowImages ?? new();
                g.ManagementTeam = payload.ManagementTeam ?? new();
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


        app.MapGet("/api/vtc/garages", async (
            [FromQuery] string guildId,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = store.GetGuild(guildId);
            var garages = BuildGaragePayload(guild.Garages);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                count = garages.Count,
                garages,
                data = garages
            });
        });

        app.MapPost("/api/vtc/garages/save", async (
            [FromQuery] string guildId,
            [FromBody] VtcGarageSaveRequest request,
            PortalDataStore store) =>
        {
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var incoming = request?.Garages ?? new List<PortalGarage>();

            var updated = store.UpdateGuild(guildId, g =>
            {
                foreach (var garage in incoming)
                {
                    NormalizeGarage(garage);

                    if (string.IsNullOrWhiteSpace(garage.Id))
                        garage.Id = !string.IsNullOrWhiteSpace(garage.CityToken)
                            ? garage.CityToken
                            : Guid.NewGuid().ToString("N");

                    if (garage.IsOwned && string.IsNullOrWhiteSpace(garage.PurchasedUtc))
                        garage.PurchasedUtc = DateTimeOffset.UtcNow.ToString("O");

                    var existing = g.Garages.FindIndex(x =>
                        string.Equals(x.Id, garage.Id, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(garage.CityToken) && string.Equals(x.CityToken, garage.CityToken, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(garage.CityName) && string.Equals(x.CityName, garage.CityName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(garage.City) && string.Equals(x.City, garage.City, StringComparison.OrdinalIgnoreCase)));

                    if (existing >= 0)
                        g.Garages[existing] = garage;
                    else
                        g.Garages.Add(garage);
                }
            });

            var garages = BuildGaragePayload(updated.Garages);
            return Results.Ok(new { ok = true, guildId, count = garages.Count, garages, data = garages });
        });


        return app;
    }

    private static List<object> BuildGaragePayload(List<PortalGarage> saved)
    {
        saved ??= new List<PortalGarage>();

        var defaults = DefaultAtsGarages();
        var byToken = saved
            .Where(g => !string.IsNullOrWhiteSpace(g.CityToken))
            .GroupBy(g => g.CityToken.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var byCity = saved
            .Where(g => !string.IsNullOrWhiteSpace(g.CityName) || !string.IsNullOrWhiteSpace(g.City))
            .GroupBy(g => (string.IsNullOrWhiteSpace(g.CityName) ? g.City : g.CityName).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<PortalGarage>();

        foreach (var garage in defaults)
        {
            PortalGarage src = garage;

            if (!string.IsNullOrWhiteSpace(garage.CityToken) && byToken.TryGetValue(garage.CityToken, out var byT))
                src = MergeGarage(garage, byT);
            else if (byCity.TryGetValue(garage.CityName, out var byC))
                src = MergeGarage(garage, byC);

            NormalizeGarage(src);
            merged.Add(src);
        }

        foreach (var custom in saved)
        {
            NormalizeGarage(custom);

            var exists = merged.Any(g =>
                string.Equals(g.CityToken, custom.CityToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.CityName, custom.CityName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.City, custom.City, StringComparison.OrdinalIgnoreCase));

            if (!exists)
                merged.Add(custom);
        }

        return merged
            .OrderByDescending(g => g.IsOwned)
            .ThenBy(g => g.State, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => string.IsNullOrWhiteSpace(g.CityName) ? g.City : g.CityName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                id = string.IsNullOrWhiteSpace(g.Id) ? g.CityToken : g.Id,
                cityToken = g.CityToken,
                city = string.IsNullOrWhiteSpace(g.CityName) ? g.City : g.CityName,
                cityName = string.IsNullOrWhiteSpace(g.CityName) ? g.City : g.CityName,
                state = g.State,
                country = string.IsNullOrWhiteSpace(g.Country) ? "USA" : g.Country,
                size = NormalizeSize(g.Size),
                capacity = CapacityForSize(g.Size),
                truckCapacity = CapacityForSize(g.Size),
                assigned = g.AssignedTruckNumbers?.Count ?? 0,
                assignedTruckNumbers = g.AssignedTruckNumbers ?? new List<string>(),
                trucks = g.AssignedTruckNumbers ?? new List<string>(),
                slots = string.IsNullOrWhiteSpace(g.Slots) ? CapacityForSize(g.Size).ToString() : g.Slots,
                owned = g.IsOwned,
                isOwned = g.IsOwned,
                mapX = g.MapX ?? g.Longitude,
                mapY = g.MapY ?? g.Latitude,
                longitude = g.Longitude ?? g.MapX,
                latitude = g.Latitude ?? g.MapY,
                iconType = g.IsOwned ? "garage-owned" : "garage",
                iconWeight = g.IsOwned ? "bold" : "normal",
                cost = g.Cost,
                purchasedBy = g.PurchasedBy,
                purchasedUtc = g.PurchasedUtc,
                notes = g.Notes
            })
            .Cast<object>()
            .ToList();
    }

    private static PortalGarage MergeGarage(PortalGarage baseGarage, PortalGarage saved)
    {
        NormalizeGarage(baseGarage);
        NormalizeGarage(saved);

        return new PortalGarage
        {
            Id = !string.IsNullOrWhiteSpace(saved.Id) ? saved.Id : baseGarage.Id,
            CityToken = !string.IsNullOrWhiteSpace(saved.CityToken) ? saved.CityToken : baseGarage.CityToken,
            CityName = !string.IsNullOrWhiteSpace(saved.CityName) ? saved.CityName : baseGarage.CityName,
            City = !string.IsNullOrWhiteSpace(saved.City) ? saved.City : baseGarage.City,
            State = !string.IsNullOrWhiteSpace(saved.State) ? saved.State : baseGarage.State,
            Country = !string.IsNullOrWhiteSpace(saved.Country) ? saved.Country : baseGarage.Country,
            Size = !string.IsNullOrWhiteSpace(saved.Size) ? saved.Size : baseGarage.Size,
            TruckCapacity = saved.TruckCapacity > 0 ? saved.TruckCapacity : baseGarage.TruckCapacity,
            IsOwned = saved.IsOwned || !string.IsNullOrWhiteSpace(saved.PurchasedUtc),
            MapX = saved.MapX ?? baseGarage.MapX,
            MapY = saved.MapY ?? baseGarage.MapY,
            Latitude = saved.Latitude ?? baseGarage.Latitude,
            Longitude = saved.Longitude ?? baseGarage.Longitude,
            AssignedTruckNumbers = saved.AssignedTruckNumbers ?? new List<string>(),
            Slots = !string.IsNullOrWhiteSpace(saved.Slots) ? saved.Slots : baseGarage.Slots,
            Cost = saved.Cost,
            PurchasedBy = saved.PurchasedBy,
            PurchasedUtc = saved.PurchasedUtc,
            Notes = saved.Notes
        };
    }

    private static void NormalizeGarage(PortalGarage g)
    {
        if (g == null) return;

        if (string.IsNullOrWhiteSpace(g.CityName))
            g.CityName = g.City ?? "";

        if (string.IsNullOrWhiteSpace(g.City))
            g.City = g.CityName ?? "";

        if (string.IsNullOrWhiteSpace(g.CityToken))
            g.CityToken = Tokenize(g.CityName);

        g.Size = NormalizeSize(g.Size);
        g.TruckCapacity = CapacityForSize(g.Size);
        g.Slots = string.IsNullOrWhiteSpace(g.Slots) ? g.TruckCapacity.ToString() : g.Slots;
        g.Country = string.IsNullOrWhiteSpace(g.Country) ? "USA" : g.Country;
        g.AssignedTruckNumbers ??= new List<string>();

        if (!g.Latitude.HasValue && g.MapY.HasValue)
            g.Latitude = g.MapY;

        if (!g.Longitude.HasValue && g.MapX.HasValue)
            g.Longitude = g.MapX;

        if (!g.MapY.HasValue && g.Latitude.HasValue)
            g.MapY = g.Latitude;

        if (!g.MapX.HasValue && g.Longitude.HasValue)
            g.MapX = g.Longitude;
    }

    private static string NormalizeSize(string? size)
    {
        var s = (size ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "large" or "big" => "Large",
            "medium" or "med" => "Medium",
            _ => "Small"
        };
    }

    private static int CapacityForSize(string? size)
    {
        return NormalizeSize(size) switch
        {
            "Large" => 7,
            "Medium" => 5,
            _ => 3
        };
    }

    private static string Tokenize(string? text)
    {
        var s = (text ?? "").Trim().ToLowerInvariant();
        var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        while (new string(chars).Contains("__"))
            chars = new string(chars).Replace("__", "_").ToCharArray();
        return new string(chars).Trim('_');
    }

    private static PortalGarage Garage(string token, string city, string state, string size, double lat, double lon)
    {
        return new PortalGarage
        {
            Id = token,
            CityToken = token,
            CityName = city,
            City = city,
            State = state,
            Country = "USA",
            Size = NormalizeSize(size),
            TruckCapacity = CapacityForSize(size),
            Slots = CapacityForSize(size).ToString(),
            Latitude = lat,
            Longitude = lon,
            MapY = lat,
            MapX = lon,
            IsOwned = false
        };
    }

    private static List<PortalGarage> DefaultAtsGarages()
    {
        return new List<PortalGarage>
        {
            Garage("seattle", "Seattle", "WA", "Small", 47.6062, -122.3321),
            Garage("spokane", "Spokane", "WA", "Small", 47.6588, -117.4260),
            Garage("portland", "Portland", "OR", "Small", 45.5152, -122.6784),
            Garage("eugene", "Eugene", "OR", "Small", 44.0521, -123.0868),
            Garage("san_francisco", "San Francisco", "CA", "Small", 37.7749, -122.4194),
            Garage("los_angeles", "Los Angeles", "CA", "Small", 34.0522, -118.2437),
            Garage("san_diego", "San Diego", "CA", "Small", 32.7157, -117.1611),
            Garage("reno", "Reno", "NV", "Small", 39.5296, -119.8138),
            Garage("las_vegas", "Las Vegas", "NV", "Small", 36.1699, -115.1398),
            Garage("phoenix", "Phoenix", "AZ", "Small", 33.4484, -112.0740),
            Garage("tucson", "Tucson", "AZ", "Small", 32.2226, -110.9747),
            Garage("albuquerque", "Albuquerque", "NM", "Small", 35.0844, -106.6504),
            Garage("denver", "Denver", "CO", "Small", 39.7392, -104.9903),
            Garage("colorado_springs", "Colorado Springs", "CO", "Small", 38.8339, -104.8214),
            Garage("fort_collins", "Fort Collins", "CO", "Small", 40.5853, -105.0844),
            Garage("salt_lake_city", "Salt Lake City", "UT", "Small", 40.7608, -111.8910),
            Garage("boise", "Boise", "ID", "Small", 43.6150, -116.2023),
            Garage("idaho_falls", "Idaho Falls", "ID", "Small", 43.4917, -112.0339),
            Garage("billings", "Billings", "MT", "Small", 45.7833, -108.5007),
            Garage("cheyenne", "Cheyenne", "WY", "Small", 41.1400, -104.8202),
            Garage("casper", "Casper", "WY", "Small", 42.8501, -106.3252),
            Garage("oklahoma_city", "Oklahoma City", "OK", "Small", 35.4676, -97.5164),
            Garage("tulsa", "Tulsa", "OK", "Small", 36.1540, -95.9928),
            Garage("dallas", "Dallas", "TX", "Small", 32.7767, -96.7970),
            Garage("fort_worth", "Fort Worth", "TX", "Small", 32.7555, -97.3308),
            Garage("houston", "Houston", "TX", "Small", 29.7604, -95.3698),
            Garage("san_antonio", "San Antonio", "TX", "Small", 29.4241, -98.4936),
            Garage("austin", "Austin", "TX", "Small", 30.2672, -97.7431),
            Garage("wichita", "Wichita", "KS", "Small", 37.6872, -97.3301),
            Garage("kansas_city", "Kansas City", "KS", "Small", 39.1141, -94.6275),
            Garage("omaha", "Omaha", "NE", "Small", 41.2565, -95.9345),
            Garage("lincoln", "Lincoln", "NE", "Small", 40.8136, -96.7026)
        };
    }
}

public sealed class VtcGarageSaveRequest
{
    public List<PortalGarage> Garages { get; set; } = new();
}

