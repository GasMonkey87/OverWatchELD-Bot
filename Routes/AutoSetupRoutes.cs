using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class AutoSetupRoutes
{
    public static void Register(IEndpointRouteBuilder app, BotServices services, JsonSerializerOptions jsonWrite)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        app.MapPost("/api/vtc/setup/auto-discord", async (HttpContext ctx) =>
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                var root = body.RootElement;

                var guildId = ReadString(root, "guildId", "GuildId");
                if (string.IsNullOrWhiteSpace(guildId))
                    return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

                if (!AuthGuard.IsLoggedIn(ctx))
                    return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

                if (!AuthGuard.CanManageGuild(ctx, guildId))
                    return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

                var guild = services.Client?.Guilds.FirstOrDefault(g => g.Id.ToString() == guildId);
                if (guild == null)
                    return Results.Json(new { ok = false, error = "BotNotInGuild" }, statusCode: 404);

                var me = guild.CurrentUser;
                if (me == null)
                    return Results.Json(new { ok = false, error = "BotMemberNotFound" }, statusCode: 500);

                if (!me.GuildPermissions.ManageChannels)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "BotMissingManageChannelsPermission"
                    }, statusCode: 403);
                }

                var categoryName = DefaultIfBlank(ReadString(root, "categoryName", "CategoryName"), "OverWatch ELD");
                var dispatchName = DefaultIfBlank(ReadString(root, "dispatchChannelName", "DispatchChannelName"), "dispatch-center");
                var announcementsName = DefaultIfBlank(ReadString(root, "announcementsChannelName", "AnnouncementsChannelName"), "announcements");
                var bolName = DefaultIfBlank(ReadString(root, "bolChannelName", "BolChannelName"), "bol-documents");
                var systemLogName = DefaultIfBlank(ReadString(root, "systemLogChannelName", "SystemLogChannelName"), "system-logs");

                var category = await EnsureCategoryAsync(guild, categoryName);
                var categoryId = category?.Id;

                var dispatchChannel = await EnsureTextChannelAsync(
                    guild,
                    categoryId,
                    dispatchName,
                    "OverWatch ELD dispatch operations");

                var announcementsChannel = await EnsureTextChannelAsync(
                    guild,
                    categoryId,
                    announcementsName,
                    "OverWatch ELD announcements");

                var bolChannel = await EnsureTextChannelAsync(
                    guild,
                    categoryId,
                    bolName,
                    "OverWatch ELD bills of lading and related files");

                var systemLogChannel = await EnsureTextChannelAsync(
                    guild,
                    categoryId,
                    systemLogName,
                    "OverWatch ELD logs and system activity");

                string? dispatchWebhookUrl = null;
                string? announcementWebhookUrl = null;

                if (me.GuildPermissions.ManageWebhooks)
                {
                    dispatchWebhookUrl = await EnsureWebhookUrlAsync(dispatchChannel, "OverWatch ELD Dispatch");
                    announcementWebhookUrl = await EnsureWebhookUrlAsync(announcementsChannel, "OverWatch ELD Announcements");
                }

                var settingsPath = Path.Combine(dataDir, $"settings_{guildId}.json");
                var settingsRoot = await LoadJsonObjectAsync(settingsPath) ?? new JsonObject();

                settingsRoot["guildId"] = guildId;
                settingsRoot["siteTitle"] = settingsRoot["siteTitle"]?.ToString() ?? $"{guild.Name} Hub";
                settingsRoot["welcomeText"] = settingsRoot["welcomeText"]?.ToString()
                    ?? $"Welcome to {guild.Name}. Sign in with Discord to access your OverWatch ELD portal.";

                var discord = settingsRoot["discord"] as JsonObject ?? new JsonObject();
                discord["dispatchChannelId"] = dispatchChannel.Id.ToString();
                discord["announcementsChannelId"] = announcementsChannel.Id.ToString();
                discord["bolChannelId"] = bolChannel.Id.ToString();
                discord["systemLogChannelId"] = systemLogChannel.Id.ToString();

                if (!string.IsNullOrWhiteSpace(dispatchWebhookUrl))
                    discord["dispatchWebhookUrl"] = dispatchWebhookUrl;

                if (!string.IsNullOrWhiteSpace(announcementWebhookUrl))
                    discord["announcementWebhookUrl"] = announcementWebhookUrl;

                settingsRoot["discord"] = discord;

                await File.WriteAllTextAsync(
                    settingsPath,
                    settingsRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                services.DispatchStore?.SetDispatchChannel(guildId, dispatchChannel.Id);
                services.DispatchStore?.SetAnnouncementChannel(guildId, announcementsChannel.Id);

                if (!string.IsNullOrWhiteSpace(dispatchWebhookUrl))
                    services.DispatchStore?.SetDispatchWebhook(guildId, dispatchWebhookUrl);

                if (!string.IsNullOrWhiteSpace(announcementWebhookUrl))
                    services.DispatchStore?.SetAnnouncementWebhook(guildId, announcementWebhookUrl);

                return Results.Ok(new
                {
                    ok = true,
                    guildId,
                    category = new
                    {
                        id = category?.Id.ToString() ?? "",
                        name = category?.Name ?? categoryName
                    },
                    channels = new
                    {
                        dispatch = new
                        {
                            id = dispatchChannel.Id.ToString(),
                            name = dispatchChannel.Name,
                            webhookUrl = dispatchWebhookUrl ?? ""
                        },
                        announcements = new
                        {
                            id = announcementsChannel.Id.ToString(),
                            name = announcementsChannel.Name,
                            webhookUrl = announcementWebhookUrl ?? ""
                        },
                        bol = new
                        {
                            id = bolChannel.Id.ToString(),
                            name = bolChannel.Name
                        },
                        systemLogs = new
                        {
                            id = systemLogChannel.Id.ToString(),
                            name = systemLogChannel.Name
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = ex.Message
                }, statusCode: 500);
            }
        });
    }

    private static async Task<ICategoryChannel?> EnsureCategoryAsync(SocketGuild guild, string name)
    {
        var existing = guild.CategoryChannels
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await guild.CreateCategoryChannelAsync(name);
    }

    private static async Task<ITextChannel> EnsureTextChannelAsync(
        SocketGuild guild,
        ulong? categoryId,
        string name,
        string topic)
    {
        var existing = guild.TextChannels.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
            x.CategoryId == categoryId);

        if (existing != null)
            return existing;

        return await guild.CreateTextChannelAsync(name, props =>
        {
            props.CategoryId = categoryId;
            props.Topic = topic;
        });
    }

    private static async Task<string?> EnsureWebhookUrlAsync(ITextChannel channel, string webhookName)
    {
        try
        {
            var hooks = await channel.GetWebhooksAsync();
            var existing = hooks.FirstOrDefault(x =>
                string.Equals(x.Name, webhookName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return BuildWebhookUrl(existing.Id, existing.Token);

            var created = await channel.CreateWebhookAsync(webhookName);
            return BuildWebhookUrl(created.Id, created.Token);
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildWebhookUrl(ulong id, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return $"https://discord.com/api/webhooks/{id}/{token}";
    }

    private static async Task<JsonObject?> LoadJsonObjectAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var raw = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return JsonNode.Parse(raw) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var p))
            {
                var s = p.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return "";
    }

    private static string DefaultIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
