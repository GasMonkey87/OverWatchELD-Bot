using System.Text.Json;
using Discord;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcDiscordAutoSetupRoutes
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string SetupFile = Path.Combine(DataDir, "vtc_discord_setup.json");

    public static void MapVtcDiscordAutoSetupRoutes(this WebApplication app)
    {
        app.MapPost("/api/vtc/setup/auto-discord", async (
            HttpContext ctx,
            DiscordSocketClient discord) =>
        {
            var guildIdText = ctx.Request.Query["guildId"].ToString();

            if (!ulong.TryParse(guildIdText, out var guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            var guild = discord.GetGuild(guildId);
            if (guild == null)
                return Results.NotFound(new { ok = false, error = "GuildNotFound" });

            Directory.CreateDirectory(DataDir);

            var category = await EnsureCategoryAsync(guild, "OverWatch ELD");

            var dispatch = await EnsureTextChannelAsync(guild, category, "eld-dispatch");
            var bol = await EnsureTextChannelAsync(guild, category, "eld-bol");
            var logs = await EnsureTextChannelAsync(guild, category, "eld-logs");
            var inspections = await EnsureTextChannelAsync(guild, category, "eld-inspections");
            var maintenance = await EnsureTextChannelAsync(guild, category, "eld-maintenance");
            var leaderboard = await EnsureTextChannelAsync(guild, category, "eld-leaderboard");
            var announcements = await EnsureTextChannelAsync(guild, category, "eld-announcements");
            var system = await EnsureTextChannelAsync(guild, category, "eld-system");

            var dispatchWebhook = await EnsureWebhookAsync(dispatch, "OverWatch ELD Dispatch");
            var bolWebhook = await EnsureWebhookAsync(bol, "OverWatch ELD BOL");
            var logsWebhook = await EnsureWebhookAsync(logs, "OverWatch ELD Logs");
            var inspectionsWebhook = await EnsureWebhookAsync(inspections, "OverWatch ELD Inspections");
            var maintenanceWebhook = await EnsureWebhookAsync(maintenance, "OverWatch ELD Maintenance");
            var leaderboardWebhook = await EnsureWebhookAsync(leaderboard, "OverWatch ELD Leaderboard");
            var announcementsWebhook = await EnsureWebhookAsync(announcements, "OverWatch ELD Announcements");
            var systemWebhook = await EnsureWebhookAsync(system, "OverWatch ELD System");

            var setup = new
            {
                ok = true,
                guildId = guild.Id.ToString(),
                guildName = guild.Name,
                categoryId = category.Id.ToString(),

                channels = new
                {
                    dispatchChannelId = dispatch.Id.ToString(),
                    bolChannelId = bol.Id.ToString(),
                    logsChannelId = logs.Id.ToString(),
                    inspectionsChannelId = inspections.Id.ToString(),
                    maintenanceChannelId = maintenance.Id.ToString(),
                    leaderboardChannelId = leaderboard.Id.ToString(),
                    announcementsChannelId = announcements.Id.ToString(),
                    systemLogChannelId = system.Id.ToString()
                },

                webhooks = new
                {
                    dispatchWebhookUrl = dispatchWebhook.GetWebhookUrl(),
                    bolWebhookUrl = bolWebhook.GetWebhookUrl(),
                    logsWebhookUrl = logsWebhook.GetWebhookUrl(),
                    inspectionsWebhookUrl = inspectionsWebhook.GetWebhookUrl(),
                    maintenanceWebhookUrl = maintenanceWebhook.GetWebhookUrl(),
                    leaderboardWebhookUrl = leaderboardWebhook.GetWebhookUrl(),
                    announcementsWebhookUrl = announcementsWebhook.GetWebhookUrl(),
                    systemWebhookUrl = systemWebhook.GetWebhookUrl()
                },

                updatedUtc = DateTime.UtcNow
            };

            var all = LoadAll();
            all[guild.Id.ToString()] = setup;

            await File.WriteAllTextAsync(
                SetupFile,
                JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

            return Results.Json(setup);
        });
    }

    private static Dictionary<string, object> LoadAll()
    {
        try
        {
            if (!File.Exists(SetupFile))
                return new Dictionary<string, object>();

            return JsonSerializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(SetupFile)) ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    private static async Task<SocketCategoryChannel> EnsureCategoryAsync(SocketGuild guild, string name)
    {
        var existing = guild.CategoryChannels
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await guild.CreateCategoryChannelAsync(name);
    }

    private static async Task<SocketTextChannel> EnsureTextChannelAsync(
        SocketGuild guild,
        SocketCategoryChannel category,
        string name)
    {
        var existing = guild.TextChannels
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            if (existing.CategoryId != category.Id)
            {
                await existing.ModifyAsync(x => x.CategoryId = category.Id);
            }

            return existing;
        }

        return await guild.CreateTextChannelAsync(name, x =>
        {
            x.CategoryId = category.Id;
            x.Topic = "Created automatically by OverWatch ELD.";
        });
    }

    private static async Task<IWebhook> EnsureWebhookAsync(SocketTextChannel channel, string name)
    {
        var hooks = await channel.GetWebhooksAsync();

        var existing = hooks.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await channel.CreateWebhookAsync(name);
    }

    private static string GetWebhookUrl(this IWebhook webhook)
    {
        return $"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}";
    }
}
