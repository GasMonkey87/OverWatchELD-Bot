using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class ManagementRoutes
{
    public static void Register(
        WebApplication app,
        BotServices services,
        DispatchMessageStore messageStore,
        DriverDisciplineStore disciplineStore)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        // 🔧 GET SETTINGS
        app.MapGet("/api/vtc/settings", (HttpRequest req) =>
        {
            var guildId = req.Query["guildId"].ToString();
            var path = Path.Combine(dataDir, $"settings_{guildId}.json");

            if (!File.Exists(path))
                return Results.Ok(new { ok = true, data = new { } });

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<object>(json);

            return Results.Ok(new { ok = true, data });
        });

        // 🔧 UPDATE SETTINGS
        app.MapPost("/api/vtc/settings/update", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(ctx.Request.Body);
            if (body == null)
                return Results.BadRequest(new { ok = false });

            var guildId = body.ContainsKey("guildId") ? body["guildId"]?.ToString() : "";

            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var path = Path.Combine(dataDir, $"settings_{guildId}.json");

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));

            return Results.Ok(new { ok = true });
        });

        // 🔥 IMPORTANT:
        // The auto-discord route was REMOVED from this file.
        // It now ONLY exists in:
        // Routes/VtcDiscordAutoSetupRoutes.cs
    }
}
