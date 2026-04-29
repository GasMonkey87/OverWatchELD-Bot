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
        app.MapPost("/api/vtc/setup/auto-discord", async (HttpContext ctx) =>
        {
            try
            {
                var discord = ctx.RequestServices.GetService<DiscordSocketClient>();

                if (discord == null)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "DiscordSocketClientNotRegistered"
                    }, statusCode: 500);
                }

                var guildIdText = ctx.Request.Query["guildId"].ToString();

                if (!ulong.TryParse(guildIdText, out var guildId))
                    return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

                var guild = discord.GetGuild(guildId);
                if (guild == null)
                    return Results.NotFound(new { ok = false, error = "GuildNotFound" });

                Directory.CreateDirectory(DataDir);

                var category = await EnsureCategoryAsync(guild, "OverWatch ELD");

                var dispatch = await EnsureTextChannelAsync(guild, category, "eld-dispatch");
                var logs = await EnsureTextChannelAsync(guild, category, "eld-logs");

                var webhook = await EnsureWebhookAsync(dispatch, "OverWatch ELD");

                return Results.Json(new
                {
                    ok = true,
                    guildId = guild.Id.ToString(),
                    guildName = guild.Name,
                    channels = new
                    {
                        dispatchChannelId = dispatch.Id.ToString(),
                        logsChannelId = logs.Id.ToString()
                    },
                    webhooks = new
                    {
                        dispatchWebhookUrl = GetWebhookUrl(webhook)
                    }
                });
            }
            catch (Discord.Net.HttpException ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "DiscordHttpException",
                    statusCode = ex.HttpCode,
                    reason = ex.Reason,
                    message = ex.Message
                }, statusCode: 500);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "AutoDiscordSetupFailed",
                    message = ex.Message,
                    type = ex.GetType().FullName
                }, statusCode: 500);
            }
        });
    }

    private static async Task<ICategoryChannel> EnsureCategoryAsync(SocketGuild guild, string name)
    {
        var existing = guild.CategoryChannels
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await guild.CreateCategoryChannelAsync(name);
    }

    private static async Task<ITextChannel> EnsureTextChannelAsync(
        SocketGuild guild,
        ICategoryChannel category,
        string name)
    {
        var existing = guild.TextChannels
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            if (existing.CategoryId != category.Id)
                await existing.ModifyAsync(x => x.CategoryId = category.Id);

            return existing;
        }

        return await guild.CreateTextChannelAsync(name, x =>
        {
            x.CategoryId = category.Id;
        });
    }

    private static async Task<IWebhook> EnsureWebhookAsync(ITextChannel channel, string name)
    {
        var hooks = await channel.GetWebhooksAsync();

        var existing = hooks.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await channel.CreateWebhookAsync(name);
    }

    private static string GetWebhookUrl(IWebhook webhook)
    {
        return $"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}";
    }
}
