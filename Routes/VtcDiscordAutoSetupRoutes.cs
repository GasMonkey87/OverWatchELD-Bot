using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class VtcDiscordAutoSetupRoutes
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string SetupFile = Path.Combine(DataDir, "vtc_discord_setup.json");

    public static void MapVtcDiscordAutoSetupRoutes(this WebApplication app, DiscordSocketClient? discordClient)
    {
        app.MapGet("/api/vtc/setup/auto-discord/test", () => Results.Json(new
        {
            ok = true,
            route = "VtcDiscordAutoSetupRoutes",
            utc = DateTime.UtcNow
        }));

        app.MapGet("/api/vtc/setup/current", (HttpContext ctx) =>
        {
            var guildId = ctx.Request.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId))
                return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

            Directory.CreateDirectory(DataDir);

            object? setup = null;
            var all = LoadAll();
            if (all.TryGetValue(guildId, out var existing))
                setup = existing;

            JsonNode? settings = null;
            try
            {
                var settingsPath = Path.Combine(DataDir, $"settings_{guildId}.json");
                if (File.Exists(settingsPath))
                    settings = JsonNode.Parse(File.ReadAllText(settingsPath));
            }
            catch
            {
            }

            return Results.Json(new
            {
                ok = true,
                guildId,
                setup,
                settings
            });
        });

        app.MapPost("/api/vtc/setup/auto-discord", async (HttpContext ctx) =>
        {
            try
            {
                var discord = discordClient;

                if (discord == null)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "DiscordSocketClientNotAvailable"
                    }, statusCode: 500);
                }

                var guildIdText = ctx.Request.Query["guildId"].ToString();
                if (string.IsNullOrWhiteSpace(guildIdText))
                    guildIdText = await ReadGuildIdFromBodyAsync(ctx);

                if (!ulong.TryParse(guildIdText, out var guildId))
                    return Results.BadRequest(new { ok = false, error = "MissingGuildId" });

                var guild = discord.GetGuild(guildId);
                if (guild == null)
                    return Results.NotFound(new { ok = false, error = "GuildNotFound" });

                Directory.CreateDirectory(DataDir);

                var roleResult = await EnsureRolesAsync(guild);
                var category = await EnsureCategoryAsync(guild, "OverWatch ELD");

                var dispatch = await EnsureTextChannelAsync(guild, category, "eld-dispatch", "Dispatch operations and driver communication.");
                var bol = await EnsureTextChannelAsync(guild, category, "eld-bol", "Bills of lading and delivery documents.");
                var logs = await EnsureTextChannelAsync(guild, category, "eld-logs", "ELD duty logs and driver log activity.");
                var inspections = await EnsureTextChannelAsync(guild, category, "eld-inspections", "Vehicle inspection reports and DVIR activity.");
                var maintenance = await EnsureTextChannelAsync(guild, category, "eld-maintenance", "Fleet maintenance and service activity.");
                var leaderboard = await EnsureTextChannelAsync(guild, category, "eld-leaderboard", "Driver leaderboard and performance summaries.");
                var announcements = await EnsureTextChannelAsync(guild, category, "eld-announcements", "VTC announcements shown in the ELD.");
                var system = await EnsureTextChannelAsync(guild, category, "eld-system", "System events and admin audit logs.");

                await ApplyChannelPermissionsAsync(dispatch, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole);
                await ApplyChannelPermissionsAsync(bol, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole);
                await ApplyChannelPermissionsAsync(logs, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole);
                await ApplyChannelPermissionsAsync(inspections, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole);
                await ApplyChannelPermissionsAsync(maintenance, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole);
                await ApplyChannelPermissionsAsync(leaderboard, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole);
                await ApplyChannelPermissionsAsync(announcements, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole, readOnlyDrivers: true);
                await ApplyChannelPermissionsAsync(system, roleResult.DriverRole, roleResult.DispatcherRole, roleResult.ManagerRole, roleResult.AdminRole, adminOnly: true);

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

                    roles = new
                    {
                        driverRoleId = roleResult.DriverRole?.Id.ToString() ?? "",
                        dispatcherRoleId = roleResult.DispatcherRole?.Id.ToString() ?? "",
                        managerRoleId = roleResult.ManagerRole?.Id.ToString() ?? "",
                        adminRoleId = roleResult.AdminRole?.Id.ToString() ?? ""
                    },

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
                        dispatchWebhookUrl = GetWebhookUrl(dispatchWebhook),
                        bolWebhookUrl = GetWebhookUrl(bolWebhook),
                        logsWebhookUrl = GetWebhookUrl(logsWebhook),
                        inspectionsWebhookUrl = GetWebhookUrl(inspectionsWebhook),
                        maintenanceWebhookUrl = GetWebhookUrl(maintenanceWebhook),
                        leaderboardWebhookUrl = GetWebhookUrl(leaderboardWebhook),
                        announcementsWebhookUrl = GetWebhookUrl(announcementsWebhook),
                        systemWebhookUrl = GetWebhookUrl(systemWebhook)
                    },

                    updatedUtc = DateTime.UtcNow
                };

                var all = LoadAll();
                all[guild.Id.ToString()] = setup;

                await File.WriteAllTextAsync(
                    SetupFile,
                    JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

                await SaveSettingsFileAsync(guild, setup);

                TryUpdateDispatchSettings(
                    ctx,
                    guild.Id.ToString(),
                    dispatch.Id,
                    announcements.Id,
                    GetWebhookUrl(dispatchWebhook),
                    GetWebhookUrl(announcementsWebhook));

                return Results.Json(setup);
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

    private static async Task<string> ReadGuildIdFromBodyAsync(HttpContext ctx)
    {
        try
        {
            ctx.Request.EnableBuffering();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            ctx.Request.Body.Position = 0;

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("guildId", out var guildIdProp))
            {
                return guildIdProp.ToString().Trim();
            }
        }
        catch
        {
            try { ctx.Request.Body.Position = 0; } catch { }
        }

        return "";
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

    private static async Task SaveSettingsFileAsync(SocketGuild guild, object setup)
    {
        var serializedSetup = JsonSerializer.Serialize(setup);
        using var setupDoc = JsonDocument.Parse(serializedSetup);
        var setupRoot = setupDoc.RootElement;

        var settingsPath = Path.Combine(DataDir, $"settings_{guild.Id}.json");
        JsonObject settingsRoot = new();

        try
        {
            if (File.Exists(settingsPath))
                settingsRoot = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            settingsRoot = new JsonObject();
        }

        settingsRoot["guildId"] = guild.Id.ToString();
        settingsRoot["siteTitle"] ??= $"{guild.Name} Hub";
        settingsRoot["welcomeText"] ??= $"Welcome to {guild.Name}. Sign in with Discord to access your OverWatch ELD portal.";

        var discord = settingsRoot["discord"] as JsonObject ?? new JsonObject();

        if (setupRoot.TryGetProperty("channels", out var channels))
            CopyJsonObject(channels, discord);

        if (setupRoot.TryGetProperty("webhooks", out var webhooks))
            CopyJsonObject(webhooks, discord);

        if (setupRoot.TryGetProperty("roles", out var roles))
            CopyJsonObject(roles, discord);

        discord["useThreadsPerDriver"] ??= true;

        settingsRoot["discord"] = discord;

        await File.WriteAllTextAsync(
            settingsPath,
            settingsRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyJsonObject(JsonElement source, JsonObject target)
    {
        foreach (var prop in source.EnumerateObject())
            target[prop.Name] = prop.Value.ToString();
    }

    private static void TryUpdateDispatchSettings(
        HttpContext ctx,
        string guildId,
        ulong dispatchChannelId,
        ulong announcementsChannelId,
        string dispatchWebhookUrl,
        string announcementsWebhookUrl)
    {
        try
        {
            var store = ctx.RequestServices.GetService<DispatchSettingsStore>();
            store?.SetDispatchChannel(guildId, dispatchChannelId);
            store?.SetAnnouncementChannel(guildId, announcementsChannelId);
            store?.SetDispatchWebhook(guildId, dispatchWebhookUrl);
            store?.SetAnnouncementWebhook(guildId, announcementsWebhookUrl);
        }
        catch
        {
        }
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
        string name,
        string topic)
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
            x.Topic = topic;
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

    private static async Task<(IRole? DriverRole, IRole? DispatcherRole, IRole? ManagerRole, IRole? AdminRole)> EnsureRolesAsync(SocketGuild guild)
    {
        var driver = await EnsureRoleAsync(guild, "OverWatch Driver");
        var dispatcher = await EnsureRoleAsync(guild, "OverWatch Dispatcher");
        var manager = await EnsureRoleAsync(guild, "OverWatch Manager");
        var admin = await EnsureRoleAsync(guild, "OverWatch Admin");

        return (driver, dispatcher, manager, admin);
    }

    private static async Task<IRole?> EnsureRoleAsync(SocketGuild guild, string name)
    {
        var existing = guild.Roles
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await guild.CreateRoleAsync(
            name,
            GuildPermissions.None,
            color: null,
            isHoisted: false,
            options: null);
    }

    private static async Task ApplyChannelPermissionsAsync(
        ITextChannel channel,
        IRole? driverRole,
        IRole? dispatcherRole,
        IRole? managerRole,
        IRole? adminRole,
        bool readOnlyDrivers = false,
        bool adminOnly = false)
    {
        try
        {
            if (driverRole != null)
            {
                var driverPerms = adminOnly
                    ? new OverwritePermissions(
                        viewChannel: PermValue.Deny,
                        sendMessages: PermValue.Deny)
                    : new OverwritePermissions(
                        viewChannel: PermValue.Allow,
                        sendMessages: readOnlyDrivers ? PermValue.Deny : PermValue.Allow,
                        readMessageHistory: PermValue.Allow);

                await channel.AddPermissionOverwriteAsync(driverRole, driverPerms);
            }

            var staffPerms = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                manageMessages: PermValue.Allow);

            if (dispatcherRole != null)
                await channel.AddPermissionOverwriteAsync(dispatcherRole, staffPerms);

            if (managerRole != null)
                await channel.AddPermissionOverwriteAsync(managerRole, staffPerms);

            if (adminRole != null)
                await channel.AddPermissionOverwriteAsync(adminRole, staffPerms);
        }
        catch
        {
            // Best effort. If Discord role hierarchy blocks overwrites,
            // setup still continues so channels/webhooks/settings are created.
        }
    }

    private static string GetWebhookUrl(IWebhook webhook)
    {
        return $"https://discord.com/api/webhooks/{webhook.Id}/{webhook.Token}";
    }
}
