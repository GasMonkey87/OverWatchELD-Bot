using System;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Models.Events;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class ApiRoutes
{
    private static readonly HashSet<string> AdminRoleNames = new(StringComparer.OrdinalIgnoreCase)
{
    "Admin",
    "Administrator",
    "Owner",
    "Founder",
    "Co-Owner",
    "Head Admin",
    "VTC Admin",
    "Company Owner",
    "Server Owner"
};

private static readonly HashSet<string> ManagerRoleNames = new(StringComparer.OrdinalIgnoreCase)
{
    "Manager",
    "Management",
    "Supervisor",
    "Fleet Manager",
    "Roster Manager",
    "Dispatch",
    "Dispatch Admin",
    "Operations",
    "Ops",
    "Lead",
    "Team Lead"
};

    public static void Register(
        IEndpointRouteBuilder app,
        BotServices services,
        JsonSerializerOptions jsonRead,
        JsonSerializerOptions jsonWrite,
        HttpClient http)
    {
        var api = app.MapGroup("/api");
        var api2 = app.MapGroup("/api/api");

        RegisterCore(api, services, jsonRead, jsonWrite, http);
        RegisterCore(api2, services, jsonRead, jsonWrite, http);
    }

    private static void RegisterCore(
        IEndpointRouteBuilder r,
        BotServices services,
        JsonSerializerOptions jsonRead,
        JsonSerializerOptions jsonWrite,
        HttpClient http)
    {
        BolUploadRoutes.Register(r, services, jsonWrite);
        BolDiscordOnlyRoutes.Register(r, services, jsonRead, jsonWrite);


        r.MapPost("/notifications/push", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<EldNotificationPushRequest>(jsonRead);

            if (req == null)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(req.GuildId))
                return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

            if (services.Client == null || !services.DiscordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            if (!ulong.TryParse(req.GuildId.Trim(), out var gid))
                return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

            var guild = services.Client.GetGuild(gid);

            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            SocketTextChannel? channel = null;

            if (ulong.TryParse((req.ChannelId ?? "").Trim(), out var cid) && cid != 0)
                channel = guild.GetTextChannel(cid);

            if (channel == null)
            {
                var desired = NormalizeNotifyChannel(req.DefaultChannelName);
                channel = guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name) == desired);
            }

            channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name) == "eld-notifications");
            channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name) == "announcements");
            channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name).Contains("system"));
            channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name).Contains("notification"));

            if (channel == null)
                return Results.Json(new { ok = false, error = "NotificationChannelNotFound" }, statusCode: 404);

            var embed = new EmbedBuilder()
                .WithTitle($"{NotifyIcon(req.Category)} {FirstNonEmpty(req.Title, "OverWatch ELD Notification")}")
                .WithDescription(FirstNonEmpty(req.Message, "Notification") + (string.IsNullOrWhiteSpace(req.Details) ? "" : "\n\n" + req.Details))
                .WithColor(NotifyColor(req.Category))
                .WithFooter($"OverWatch ELD • {FirstNonEmpty(req.Category, "System")}")
                .WithCurrentTimestamp()
                .Build();

            await channel.SendMessageAsync(embed: embed);

            return Results.Json(new
            {
                ok = true,
                channelId = channel.Id.ToString(),
                channelName = channel.Name
            }, jsonWrite);
        });

        r.MapGet("/fleet/truck-approved", () => Results.Json(new
        {
            ok = true,
            route = "/api/fleet/truck-approved",
            method = "POST",
            deployed = true
        }, jsonWrite));
        
        r.MapPost("/fleet/truck-approved", async (HttpContext ctx) =>
{
    try
    {
        var req = await ctx.Request.ReadFromJsonAsync<TruckApprovedDiscordRequest>(jsonRead);

        if (req == null)
            return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

        if (string.IsNullOrWhiteSpace(req.GuildId))
            return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

        if (services.Client == null || !services.DiscordReady)
            return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

        if (!ulong.TryParse(req.GuildId.Trim(), out var gid))
            return Results.Json(new { ok = false, error = "BadGuildId", guildId = req.GuildId }, statusCode: 400);

        var guild = services.Client.GetGuild(gid);
        if (guild == null)
            return Results.Json(new { ok = false, error = "GuildNotFound", guildId = req.GuildId }, statusCode: 404);

        var channel = guild.TextChannels.FirstOrDefault(c =>
        {
            var n = (c.Name ?? "").ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
            return n == "fleet-trucks" || n == "fleet" || n == "vtc-fleet" || n == "trucks" || n.Contains("fleet") || n.Contains("truck");
        });

        if (channel == null)
            return Results.Json(new { ok = false, error = "FleetChannelNotFound", channels = guild.TextChannels.Select(c => c.Name).ToArray() }, statusCode: 404);

        var embed = new EmbedBuilder()
            .WithTitle("🚛 New Fleet Truck Approved")
            .WithColor(Color.Green)
            .AddField("Truck #", FirstNonEmpty(req.TruckNumber, "N/A"), true)
            .AddField("Driver", FirstNonEmpty(req.DriverName, "Unassigned"), true)
            .AddField("Truck", FirstNonEmpty(req.TruckName, req.Model, "Unknown"), true)
            .AddField("Plate", FirstNonEmpty(req.Plate, "N/A"), true)
            .AddField("Mileage", FirstNonEmpty(req.Mileage, "N/A"), true)
            .AddField("Status", "Approved", true)
            .WithFooter("OverWatch ELD Fleet Management")
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);

        return Results.Json(new { ok = true, channelId = channel.Id.ToString(), channelName = channel.Name }, jsonWrite);
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            ok = false,
            error = "FleetTruckApprovedRouteException",
            message = ex.Message,
            type = ex.GetType().FullName,
            stack = ex.StackTrace
        }, statusCode: 500);
    }
});
        
        r.MapGet("/vtc/servers", () =>
        {
            if (services.Client == null || !services.DiscordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var servers = services.Client.Guilds.Select(g => new
            {
                id = g.Id.ToString(),
                name = g.Name,
                guildId = g.Id.ToString()
            }).ToArray();

            return Results.Json(new { ok = true, servers, serverCount = servers.Length }, jsonWrite);
        });

        r.MapGet("/vtc/name", (HttpRequest req) =>
        {
            var guild = DiscordThreadService.ResolveGuild(services.Client, req.Query["guildId"].ToString());
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            return Results.Json(new
            {
                ok = true,
                guildId = guild.Id.ToString(),
                name = guild.Name,
                vtcName = guild.Name
            }, jsonWrite);
        });

        r.MapGet("/vtc/me", async (HttpRequest req) =>
        {
            var guild = DiscordThreadService.ResolveGuild(services.Client, req.Query["guildId"].ToString());
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var discordUserId = (req.Query["discordUserId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(discordUserId, out var uid) || uid == 0)
                return Results.Json(new { ok = false, error = "MissingDiscordUserId" }, statusCode: 400);

            try { await guild.DownloadUsersAsync(); } catch { }

            var user = guild.GetUser(uid);
            if (user == null)
                return Results.Json(new { ok = false, error = "MemberNotFound" }, statusCode: 404);

            string storedRole = "Driver";
            try
            {
                var manual = services.RosterStore?.List(guild.Id.ToString());
                var hit = manual?.FirstOrDefault(x =>
                    string.Equals((x.DiscordUserId ?? "").Trim(), discordUserId, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(hit?.Role))
                    storedRole = hit.Role.Trim();
            }
            catch { }

            var resolvedRole = ResolveGuildRole(guild, user, storedRole);

            return Results.Json(new
            {
                ok = true,
                guildId = guild.Id.ToString(),
                discordUserId = user.Id.ToString(),
                discordUsername = user.Username,
                displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
                storedRole,
                resolvedRole,
                canManageRoster = CanManageRoster(resolvedRole),
                canConfigureVtc = CanConfigureVtc(resolvedRole),
                isGuildOwner = guild.OwnerId == user.Id
            }, jsonWrite);
        });
                r.MapGet("/vtc/role", async (HttpRequest req) =>
        {
            var guild = DiscordThreadService.ResolveGuild(services.Client, req.Query["guildId"].ToString());
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var discordUserId = (req.Query["discordUserId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(discordUserId, out var uid) || uid == 0)
                return Results.Json(new { ok = false, error = "MissingDiscordUserId" }, statusCode: 400);

            try { await guild.DownloadUsersAsync(); } catch { }

            var user = guild.GetUser(uid);
            if (user == null)
                return Results.Json(new { ok = false, error = "MemberNotFound" }, statusCode: 404);

            string storedRole = "Driver";

            try
            {
                var manual = services.RosterStore?.List(guild.Id.ToString());
                var hit = manual?.FirstOrDefault(x =>
                    string.Equals((x.DiscordUserId ?? "").Trim(), discordUserId, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(hit?.Role))
                    storedRole = hit.Role.Trim();
            }
            catch { }

            var resolvedRole = ResolveGuildRole(guild, user, storedRole);

            return Results.Json(new
            {
                ok = true,
                guildId = guild.Id.ToString(),
                discordUserId = user.Id.ToString(),
                discordUsername = user.Username,
                displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
                role = resolvedRole,
                resolvedRole,
                canManageRoster = CanManageRoster(resolvedRole),
                canConfigureVtc = CanConfigureVtc(resolvedRole),
                isGuildOwner = guild.OwnerId == user.Id
            }, jsonWrite);
        });
        r.MapGet("/vtc/pair/claim", (HttpRequest req) =>
        {
            var code = (req.Query["code"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                return Results.Json(new { ok = false, error = "MissingCode" }, statusCode: 400);

            if (services.LinkCodeStore == null)
                return Results.Json(new { ok = false, error = "LinkStoreNotReady" }, statusCode: 503);

            if (!services.LinkCodeStore.Consume(code, out var entry))
                return Results.Json(new { ok = false, error = "InvalidOrExpiredCode" }, statusCode: 404);

            try
            {
                services.LinkedDriversStore?.Link(entry.GuildId, entry.DiscordUserId, entry.DiscordUsername, entry.Code);
            }
            catch { }

            return Results.Json(new
            {
                ok = true,
                code = entry.Code,
                guildId = entry.GuildId,
                vtcName = string.IsNullOrWhiteSpace(entry.GuildName) ? "VTC" : entry.GuildName,
                discordUserId = entry.DiscordUserId,
                discordUsername = entry.DiscordUsername
            }, jsonWrite);
        });

        r.MapGet("/vtc/roster", async (HttpRequest req) =>
        {
            var guild = DiscordThreadService.ResolveGuild(services.Client, req.Query["guildId"].ToString());
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            if (services.RosterStore == null)
                return Results.Json(new { ok = false, error = "RosterNotReady" }, statusCode: 503);

            try
            {
                try { await guild.DownloadUsersAsync(); } catch { }

                var manual = services.RosterStore.List(guild.Id.ToString());

                var merged = RosterMerge.BuildMergedDiscordRoster(guild, manual)
                    .OrderByDescending(x => x.RoleSort)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x =>
                    {
                        var resolvedRole = "Driver";

                        try
                        {
                            SocketGuildUser? user = null;

                            if (!string.IsNullOrWhiteSpace(x.DiscordUserId) &&
                                ulong.TryParse(x.DiscordUserId, out var uid))
                            {
                                user = guild.GetUser(uid);
                            }

                            resolvedRole = ResolveGuildRole(guild, user, x.Role);
                        }
                        catch
                        {
                            resolvedRole = string.IsNullOrWhiteSpace(x.Role) ? "Driver" : x.Role.Trim();
                        }

                        return new
                        {
                            driverId = x.DriverId,
                            name = x.Name,
                            driverName = x.Name,
                            discordUserId = x.DiscordUserId ?? "",
                            discordUsername = x.DiscordUsername ?? "",
                            truckNumber = x.TruckNumber ?? "",
                            role = resolvedRole,
                            canManageRoster = CanManageRoster(resolvedRole),
                            canConfigureVtc = CanConfigureVtc(resolvedRole),
                            status = x.Status ?? "",
                            notes = x.Notes ?? "",
                            createdUtc = x.CreatedUtc,
                            updatedUtc = x.UpdatedUtc
                        };
                    })
                    .ToArray();

                return Results.Json(new { ok = true, guildId = guild.Id.ToString(), drivers = merged }, jsonWrite);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "RosterBuildFailed", message = ex.Message }, statusCode: 500);
            }
        });
r.MapGet("/vtc/settings", async (HttpRequest req, GuildSettingsStore store) =>
{
    var guildId = (req.Query["guildId"].ToString() ?? "").Trim();

    if (string.IsNullOrWhiteSpace(guildId))
        return Results.Json(new { ok = false, error = "MissingGuildId" });

    var s = await store.GetAsync(guildId);

    return Results.Json(new
    {
        ok = true,
        settings = s
    }, jsonWrite);
});

        r.MapGet("/vtc/announcements", async (HttpRequest req) =>
        {
            if (services.Client == null || !services.DiscordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady", announcements = Array.Empty<object>() }, statusCode: 503);

            var guild = DiscordThreadService.ResolveGuild(services.Client, req.Query["guildId"].ToString());
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound", announcements = Array.Empty<object>() }, statusCode: 404);

            try
            {
                var settings = services.DispatchStore?.Get(guild.Id.ToString());

                var configuredChannelId = FirstNonEmpty(
                    ReadObjString(settings, "AnnouncementChannelId"),
                    ReadObjString(settings, "AnnouncementsChannelId"),
                    req.Query["channelId"].ToString()
                );

                SocketTextChannel? channel = null;

                if (ulong.TryParse(configuredChannelId, out var chId) && chId != 0)
                    channel = guild.GetTextChannel(chId);

                channel ??= FindAnnouncementChannel(guild);

                if (channel == null)
                    return Results.Json(new { ok = true, announcements = Array.Empty<object>() }, jsonWrite);

                var msgs = await channel.GetMessagesAsync(25).FlattenAsync();

                var items = msgs
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .OrderByDescending(m => m.Timestamp)
                    .Select(m => new
                    {
                        id = m.Id.ToString(),
                        author = m.Author?.Username ?? "Announcement",
                        text = m.Content ?? "",
                        message = m.Content ?? "",
                        body = m.Content ?? "",
                        createdUtc = m.Timestamp.UtcDateTime.ToString("o"),
                        channelId = channel.Id.ToString(),
                        channelName = channel.Name
                    })
                    .ToArray();

                return Results.Json(new
                {
                    ok = true,
                    announcements = items
                }, jsonWrite);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "AnnouncementsReadFailed",
                    message = ex.Message,
                    announcements = Array.Empty<object>()
                }, statusCode: 500);
            }
        });

        r.MapMethods("/vtc/announcements/post", new[] { "POST", "GET" }, async (HttpRequest req) =>
        {
            AnnouncementPostReq? payload = null;
            IFormCollection? form = null;

            try
            {
                if (req.HasFormContentType)
                    form = await req.ReadFormAsync();
            }
            catch
            {
                form = null;
            }

            if (HttpMethods.IsPost(req.Method) && !req.HasFormContentType)
            {
                try
                {
                    payload = await JsonSerializer.DeserializeAsync<AnnouncementPostReq>(req.Body, jsonRead);
                }
                catch
                {
                    payload = null;
                }
            }

            var guildId = FirstNonEmpty(
                ReadObjString(payload, "GuildId"),
                req.Query["guildId"].ToString(),
                form?["guildId"].ToString()
            );

            var guild = DiscordThreadService.ResolveGuild(services.Client, guildId);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var text = FirstNonEmpty(
                ReadObjString(payload, "Text", "Message", "Body", "Content"),
                req.Query["text"].ToString(),
                req.Query["message"].ToString(),
                req.Query["body"].ToString(),
                req.Query["content"].ToString(),
                form?["text"].ToString(),
                form?["message"].ToString(),
                form?["body"].ToString(),
                form?["content"].ToString()
            );

            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "EmptyText" }, statusCode: 400);

            var author = FirstNonEmpty(
                ReadObjString(payload, "Author", "DriverName", "DisplayName", "UserName", "DiscordUsername"),
                req.Query["author"].ToString(),
                form?["author"].ToString(),
                "OverWatch ELD"
            );

            var title = FirstNonEmpty(
                ReadObjString(payload, "Title"),
                req.Query["title"].ToString(),
                form?["title"].ToString()
            );

            var content = string.IsNullOrWhiteSpace(title)
                ? $"📢 **{author}**\n\n{text}"
                : $"📢 **{author}**\n\n**{title}**\n{text}";

            var settings = services.DispatchStore?.Get(guild.Id.ToString());

            var hookUrl = ReadObjString(settings, "AnnouncementWebhookUrl");
            if (!string.IsNullOrWhiteSpace(hookUrl))
            {
                try
                {
                    var hookJson = JsonSerializer.Serialize(new
                    {
                        username = "OverWatch ELD",
                        content
                    }, jsonWrite);

                    using var resp = await http.PostAsync(
                        hookUrl,
                        new StringContent(hookJson, Encoding.UTF8, "application/json"));

                    if (resp.IsSuccessStatusCode)
                        return Results.Json(new { ok = true, mode = "webhook" }, jsonWrite);
                }
                catch
                {
                }
            }

            var configuredChannelId = FirstNonEmpty(
                ReadObjString(settings, "AnnouncementChannelId"),
                ReadObjString(settings, "AnnouncementsChannelId"),
                req.Query["channelId"].ToString(),
                form?["channelId"].ToString()
            );

            SocketTextChannel? channel = null;

            if (ulong.TryParse(configuredChannelId, out var postChannelId) && postChannelId != 0)
                channel = guild.GetTextChannel(postChannelId);

            channel ??= FindAnnouncementChannel(guild);

            if (channel == null)
                return Results.Json(new { ok = false, error = "NoChannel" }, statusCode: 400);

            await channel.SendMessageAsync(content);

            return Results.Json(new
            {
                ok = true,
                mode = "channel",
                channel = channel.Name,
                channelId = channel.Id.ToString()
            }, jsonWrite);
        });



        r.MapPost("/vtc/events/announce", async (HttpRequest req) =>
        {
            EventAnnouncementReq? payload = null;
            IFormCollection? form = null;

            try
            {
                if (req.HasFormContentType)
                    form = await req.ReadFormAsync();
            }
            catch
            {
                form = null;
            }

            try
            {
                payload = await JsonSerializer.DeserializeAsync<EventAnnouncementReq>(req.Body, jsonRead);
            }
            catch
            {
                payload = null;
            }

            var guildId = FirstNonEmpty(
                ReadObjString(payload, "GuildId"),
                req.Query["guildId"].ToString(),
                form?["guildId"].ToString()
            );

            var guild = DiscordThreadService.ResolveGuild(services.Client, guildId);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var title = FirstNonEmpty(
                ReadObjString(payload, "Title"),
                req.Query["title"].ToString(),
                form?["title"].ToString()
            );

            if (string.IsNullOrWhiteSpace(title))
                return Results.Json(new { ok = false, error = "TitleRequired" }, statusCode: 400);

            var description = FirstNonEmpty(
                ReadObjString(payload, "Description", "Notes", "Text", "Message", "Body", "Content"),
                req.Query["description"].ToString(),
                req.Query["notes"].ToString(),
                req.Query["text"].ToString(),
                req.Query["message"].ToString(),
                req.Query["body"].ToString(),
                req.Query["content"].ToString(),
                form?["description"].ToString(),
                form?["notes"].ToString(),
                form?["text"].ToString(),
                form?["message"].ToString(),
                form?["body"].ToString(),
                form?["content"].ToString()
            );

            var location = FirstNonEmpty(
                ReadObjString(payload, "Location"),
                req.Query["location"].ToString(),
                form?["location"].ToString()
            );

            var startLocal = FirstNonEmpty(
                ReadObjString(payload, "StartLocal", "Start", "StartTime"),
                req.Query["startLocal"].ToString(),
                req.Query["start"].ToString(),
                form?["startLocal"].ToString(),
                form?["start"].ToString()
            );

            var endLocal = FirstNonEmpty(
                ReadObjString(payload, "EndLocal", "End", "EndTime"),
                req.Query["endLocal"].ToString(),
                req.Query["end"].ToString(),
                form?["endLocal"].ToString(),
                form?["end"].ToString()
            );

            var createdBy = FirstNonEmpty(
                ReadObjString(payload, "CreatedBy", "Author", "Host", "DriverName", "DisplayName", "UserName", "DiscordUsername"),
                req.Query["createdBy"].ToString(),
                req.Query["author"].ToString(),
                req.Query["host"].ToString(),
                form?["createdBy"].ToString(),
                form?["author"].ToString(),
                form?["host"].ToString(),
                "OverWatch ELD"
            );

            var mentionText = FirstNonEmpty(
                ReadObjString(payload, "MentionText"),
                req.Query["mentionText"].ToString(),
                form?["mentionText"].ToString()
            );

            var settings = services.DispatchStore?.Get(guild.Id.ToString());

            var hookUrl = FirstNonEmpty(
                ReadObjString(settings, "EventWebhookUrl"),
                ReadObjString(settings, "AnnouncementWebhookUrl")
            );

            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(mentionText))
                lines.Add(mentionText.Trim());

            lines.Add($"📢 **New Event Created: {title}**");

            if (!string.IsNullOrWhiteSpace(description))
                lines.Add(description.Trim());

            if (!string.IsNullOrWhiteSpace(location))
                lines.Add($"**Location:** {location.Trim()}");

            if (!string.IsNullOrWhiteSpace(startLocal))
                lines.Add($"**Start:** {startLocal.Trim()}");

            if (!string.IsNullOrWhiteSpace(endLocal))
                lines.Add($"**End:** {endLocal.Trim()}");

            if (!string.IsNullOrWhiteSpace(createdBy))
                lines.Add($"**Created By:** {createdBy.Trim()}");

            var content = string.Join("\n", lines.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (!string.IsNullOrWhiteSpace(hookUrl))
            {
                try
                {
                    var hookJson = JsonSerializer.Serialize(new
                    {
                        username = "OverWatch ELD",
                        content
                    }, jsonWrite);

                    using var resp = await http.PostAsync(
                        hookUrl,
                        new StringContent(hookJson, Encoding.UTF8, "application/json"));

                    if (resp.IsSuccessStatusCode)
                        return Results.Json(new { ok = true, mode = "webhook" }, jsonWrite);
                }
                catch
                {
                }
            }

            var configuredChannelId = FirstNonEmpty(
                ReadObjString(settings, "AnnouncementChannelId"),
                ReadObjString(settings, "AnnouncementsChannelId"),
                req.Query["channelId"].ToString(),
                form?["channelId"].ToString()
            );

            SocketTextChannel? channel = null;

            if (ulong.TryParse(configuredChannelId, out var postChannelId) && postChannelId != 0)
                channel = guild.GetTextChannel(postChannelId);

            channel ??= FindAnnouncementChannel(guild);

            if (channel == null)
                return Results.Json(new { ok = false, error = "NoChannel" }, statusCode: 400);

            await channel.SendMessageAsync(content);

            return Results.Json(new
            {
                ok = true,
                mode = "channel",
                channel = channel.Name,
                channelId = channel.Id.ToString()
            }, jsonWrite);
        });

        r.MapGet("/messages", async (HttpRequest req) =>
{
    if (services.Client == null || !services.DiscordReady)
        return Results.Json(Array.Empty<object>(), jsonWrite);

    var guild = DiscordThreadService.ResolveGuild(services.Client, req.Query["guildId"].ToString());
    if (guild == null) return Results.Json(Array.Empty<object>(), jsonWrite);

    var settings = services.GuildSettingsStore != null
        ? await services.GuildSettingsStore.GetAsync(guild.Id.ToString())
        : null;

    if (!ulong.TryParse(settings?.DispatchChannelId, out var dispatchChannelId) || dispatchChannelId == 0)
        return Results.Json(Array.Empty<object>(), jsonWrite);

    var dispatchChannel = guild.GetTextChannel(dispatchChannelId);
    if (dispatchChannel == null)
        return Results.Json(Array.Empty<object>(), jsonWrite);

    var results = new List<object>();

    var threads = await dispatchChannel.GetActiveThreadsAsync();
    var allThreads = threads.ToList();

    foreach (var thread in allThreads)
    {
        try
        {
            var msgs = await thread.GetMessagesAsync(50).FlattenAsync();

            foreach (var m in msgs.Where(x => !string.IsNullOrWhiteSpace(x.Content)))
            {
                var content = m.Content.Trim();

                results.Add(new
                {
                    id = m.Id.ToString(),
                    createdUtc = m.Timestamp.UtcDateTime.ToString("o"),
                    text = content,
                    message = content,
                    body = content,
                    content,
                    author = m.Author?.Username ?? "Unknown",
                    discordUserId = m.Author?.Id.ToString() ?? "",
                    threadId = thread.Id.ToString(),
                    isRead = true
                });
            }
        }
        catch { }
    }

    return Results.Json(
        results.OrderBy(x =>
        {
            try
            {
                var p = x.GetType().GetProperty("createdUtc");
                var s = p?.GetValue(x)?.ToString() ?? "";
                return DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.MinValue;
            }
            catch
            {
                return DateTimeOffset.MinValue;
            }
        }),
        jsonWrite
    );
});

        r.MapMethods("/messages/send", new[] { "POST", "GET" }, async (HttpRequest req) =>
        {
            if (services.Client == null || !services.DiscordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            SendMessageReq? payload = null;

            if (HttpMethods.IsPost(req.Method))
            {
                try
                {
                    payload = await JsonSerializer.DeserializeAsync<SendMessageReq>(req.Body, jsonRead);
                }
                catch
                {
                    payload = null;
                }
            }

            var text = FirstNonEmpty(
                ReadObjString(payload, "Text", "Body", "Message", "Content"),
                await ReadRequestValueAsync(req, "text", "message", "body", "content", "Text", "Message", "Body", "Content")
            );

            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BadJson",
                    hint = "Expected Text, Body, Message, or Content"
                }, statusCode: 400);
            }

            var gidStr = FirstNonEmpty(
                ReadObjString(payload, "GuildId"),
                await ReadRequestValueAsync(req, "guildId", "GuildId"));

            var guild = DiscordThreadService.ResolveGuild(services.Client, gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var senderName = FirstNonEmpty(
                DiscordThreadService.NormalizeDisplayName(
                    ReadObjString(payload, "DisplayName"),
                    ReadObjString(payload, "DiscordUsername")),
                ReadObjString(payload, "UserName", "DriverName", "From", "Sender", "SenderName", "DispatchName"),
                await ReadRequestValueAsync(req, "displayName", "discordUsername", "userName", "driverName", "from", "sender", "senderName", "dispatchName"),
                "ELD");

            var routeToken = FirstNonEmpty(
                ReadObjString(payload, "To"),
                ReadObjString(payload, "Recipient"),
                ReadObjString(payload, "Route", "Target", "Destination"),
                await ReadRequestValueAsync(req, "to", "recipient", "route", "target", "destination")
            );

            var routeToDispatch =
                string.Equals(routeToken, "dispatch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(routeToken, "dispatcher", StringComparison.OrdinalIgnoreCase);

            ulong targetUserId = 0;

            if (routeToDispatch)
            {
                var senderIdStr = FirstNonEmpty(
                    ReadObjString(payload, "UserId", "DiscordUserId"),
                    await ReadRequestValueAsync(req, "userId", "discordUserId", "UserId", "DiscordUserId"));

                if (!ulong.TryParse(senderIdStr, out targetUserId) || targetUserId == 0)
                    targetUserId = await DiscordThreadService.ResolveTargetDriverUserIdAsync(guild, payload!);
            }
            else
            {
                payload ??= new SendMessageReq();

                if (string.IsNullOrWhiteSpace(ReadObjString(payload, "DriverDiscordUserId")))
                    payload.DriverDiscordUserId = await ReadRequestValueAsync(req, "driverDiscordUserId", "DriverDiscordUserId");

                if (string.IsNullOrWhiteSpace(ReadObjString(payload, "Recipient")))
                    payload.Recipient = await ReadRequestValueAsync(req, "recipient", "Recipient");

                if (string.IsNullOrWhiteSpace(ReadObjString(payload, "DriverName")))
                    payload.DriverName = await ReadRequestValueAsync(req, "driverName", "DriverName");

                if (string.IsNullOrWhiteSpace(ReadObjString(payload, "DiscordUsername")))
                    payload.DiscordUsername = await ReadRequestValueAsync(req, "discordUsername", "DiscordUsername");

                targetUserId = await DiscordThreadService.ResolveTargetDriverUserIdAsync(guild, payload);
            }

            if (targetUserId == 0)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "DriverTargetNotResolved",
                    hint = routeToDispatch
                        ? "Send userId or discordUserId when routing to dispatch."
                        : "Send driverDiscordUserId, recipient, driverName, or discordUsername."
                }, statusCode: 400);
            }

            var targetDisplay = DiscordThreadService.ResolveDriverDisplayName(guild, targetUserId, payload!);
            var threadId = DiscordThreadService.ThreadStoreTryGet(services.ThreadStore, guild.Id, targetUserId);

            if (threadId == 0)
            {
                var created = await DiscordThreadService.EnsureDriverThreadAsync(
                    services.GuildSettingsStore,
                    services.DispatchStore,
                    services.ThreadStore,
                    guild,
                    targetUserId,
                    targetDisplay);

                if (created == 0)
                    return Results.Json(new { ok = false, error = "ThreadCreateFailedOrDispatchNotSet" }, statusCode: 500);

                threadId = created;
            }

            var chan = await DiscordThreadService.ResolveChannelAsync(services.Client, threadId);
            if (chan == null)
                return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

            await DiscordThreadService.EnsureThreadOpenAsync(chan);

            var loadNo = FirstNonEmpty(
                ReadObjString(payload, "LoadNumber", "LoadNo", "CurrentLoadNumber"),
                await ReadRequestValueAsync(req, "loadNumber", "loadNo", "currentLoadNumber", "LoadNumber", "LoadNo", "CurrentLoadNumber"));

            var truckId = FirstNonEmpty(
                ReadObjString(payload, "TruckId", "TruckNumber", "AssignedTruck", "AssignedTruckId"),
                await ReadRequestValueAsync(req, "truckId", "truckNumber", "assignedTruck", "assignedTruckId", "TruckId", "TruckNumber", "AssignedTruck", "AssignedTruckId"));

            var source = FirstNonEmpty(
                ReadObjString(payload, "Source"),
                await ReadRequestValueAsync(req, "source", "Source"),
                "eld");

            var prefixParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(loadNo)) prefixParts.Add($"Load {loadNo}");
            if (!string.IsNullOrWhiteSpace(truckId)) prefixParts.Add($"Truck {truckId}");
            if (!string.IsNullOrWhiteSpace(source)) prefixParts.Add(source);

            var prefix = prefixParts.Count == 0
                ? ""
                : $"[{string.Join(" • ", prefixParts)}] ";

            var finalText = $"**{senderName} → {(routeToDispatch ? "Dispatch" : targetDisplay)}:** {prefix}{text}";
            var sent = await chan.SendMessageAsync(finalText);

            return Results.Json(new
            {
                ok = true,
                mode = "thread",
                route = routeToDispatch ? "dispatch" : "driver",
                threadId = threadId.ToString(),
                driverDiscordUserId = targetUserId.ToString(),
                driver = targetDisplay,
                messageId = sent.Id.ToString()
            }, jsonWrite);
        });

        r.MapPost("/messages/markread/bulk", async (HttpRequest req) =>
        {
            MarkBulkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<MarkBulkReq>(req.Body, jsonRead); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                payload.MessageIds == null ||
                payload.MessageIds.Count == 0)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await DiscordThreadService.ResolveChannelAsync(services.Client, channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            int okCount = 0;
            foreach (var idStr in payload.MessageIds)
            {
                if (!ulong.TryParse(idStr, out var mid)) continue;
                try
                {
                    var msg = await chan.GetMessageAsync(mid);
                    if (msg == null) continue;
                    await msg.AddReactionAsync(new Emoji("✅"));
                    okCount++;
                }
                catch { }
            }

            return Results.Json(new { ok = true, marked = okCount }, jsonWrite);
        });

        r.MapDelete("/messages/delete/bulk", async (HttpRequest req) =>
        {
            DeleteBulkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<DeleteBulkReq>(req.Body, jsonRead); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                payload.MessageIds == null ||
                payload.MessageIds.Count == 0)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await DiscordThreadService.ResolveChannelAsync(services.Client, channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            int okCount = 0;
            foreach (var idStr in payload.MessageIds)
            {
                if (!ulong.TryParse(idStr, out var mid)) continue;
                try { await chan.DeleteMessageAsync(mid); okCount++; } catch { }
            }

            return Results.Json(new { ok = true, deleted = okCount }, jsonWrite);
        });
    }

    private static async Task<string> ReadRequestValueAsync(HttpRequest req, params string[] names)
    {
        foreach (var name in names)
        {
            var q = req.Query[name].ToString();
            if (!string.IsNullOrWhiteSpace(q))
                return q.Trim();
        }

        try
        {
            if (req.HasFormContentType)
            {
                var form = await req.ReadFormAsync();
                foreach (var name in names)
                {
                    var v = form[name].ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
            }
        }
        catch
        {
        }

        return "";
    }

    private static string ReadObjString(object? obj, params string[] names)
    {
        try
        {
            if (obj == null) return "";

            var t = obj.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p == null) continue;

                var v = p.GetValue(obj)?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
        }
        catch
        {
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            var s = (v ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }

        return "";
    }

    private static string ResolveGuildRole(SocketGuild guild, SocketGuildUser? user, string? storedRole)
    {
        try
        {
            if (user != null)
            {
                if (guild.OwnerId == user.Id)
                    return "Owner";

                var names = user.Roles
                    .Where(r => !string.Equals(r.Name, "@everyone", StringComparison.OrdinalIgnoreCase))
                    .Select(r => (r.Name ?? "").Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (names.Any(x =>
    AdminRoleNames.Contains(x) ||
    x.ToLower().Contains("admin") ||
    x.ToLower().Contains("owner")))
    return "Admin";

if (names.Any(x =>
    ManagerRoleNames.Contains(x) ||
    x.ToLower().Contains("manager") ||
    x.ToLower().Contains("management") ||
    x.ToLower().Contains("dispatch")))
    return "Manager";
            }
        }
        catch { }

        var fallback = (storedRole ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;

        return "Driver";
    }

    private static bool CanManageRoster(string role)
    {
        var r = (role ?? "").Trim();
        return r.Equals("Owner", StringComparison.OrdinalIgnoreCase) ||
               r.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               r.Equals("Manager", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanConfigureVtc(string role)
    {
        var r = (role ?? "").Trim();
        return r.Equals("Owner", StringComparison.OrdinalIgnoreCase) ||
               r.Equals("Admin", StringComparison.OrdinalIgnoreCase);
    }

    private static SocketTextChannel? FindAnnouncementChannel(SocketGuild guild)
    {
        try
        {
            var preferred = guild.TextChannels.FirstOrDefault(c =>
                string.Equals(c.Name, "announcements", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "announcement", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "vtc-announcements", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "vtc-announcement", StringComparison.OrdinalIgnoreCase));

            if (preferred != null)
                return preferred;

            return guild.TextChannels.FirstOrDefault(c =>
                c.Name.Contains("announc", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("news", StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains("updates", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}



public sealed class TruckApprovedDiscordRequest
{
    public string GuildId { get; set; } = "";
    public string FleetChannelId { get; set; } = "";
    public string TruckNumber { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string TruckName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Plate { get; set; } = "";
    public string Mileage { get; set; } = "";
}

public sealed class EldNotificationPushRequest
{
    public string GuildId { get; set; } = "";
    public string Category { get; set; } = "System";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Details { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string DefaultChannelName { get; set; } = "eld-notifications";
}

