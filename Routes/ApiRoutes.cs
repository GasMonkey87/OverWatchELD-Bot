using System;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
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

            // Bulletproof parser: read the raw JSON once, then pull values by exact OR case-insensitive key.
            // This prevents the old BadJson loop when the desktop app posts valid JSON but the typed binder misses it.
            string rawBody = "";
            if (HttpMethods.IsPost(req.Method))
            {
                try
                {
                    using var reader = new System.IO.StreamReader(req.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
                    rawBody = await reader.ReadToEndAsync();
                }
                catch
                {
                    rawBody = "";
                }
            }

            using JsonDocument? doc = !string.IsNullOrWhiteSpace(rawBody) ? JsonDocument.Parse(rawBody) : null;

            string BodyVal(params string[] names)
            {
                try
                {
                    if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                        return "";

                    foreach (var name in names)
                    {
                        if (doc.RootElement.TryGetProperty(name, out var direct) && direct.ValueKind != JsonValueKind.Null)
                            return direct.ToString().Trim();
                    }

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        foreach (var name in names)
                        {
                            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind != JsonValueKind.Null)
                                return prop.Value.ToString().Trim();
                        }
                    }
                }
                catch
                {
                }

                return "";
            }

            string QueryVal(params string[] names)
            {
                foreach (var name in names)
                {
                    var v = req.Query[name].ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                return "";
            }

            var text = FirstNonEmpty(
                BodyVal("Text", "Body", "Message", "Content", "text", "body", "message", "content"),
                QueryVal("Text", "Body", "Message", "Content", "text", "body", "message", "content")
            );

            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BadJson",
                    hint = "Expected Text, Body, Message, or Content in the JSON body.",
                    receivedBodyLength = rawBody?.Length ?? 0
                }, statusCode: 400);
            }

            var gidStr = FirstNonEmpty(BodyVal("GuildId", "guildId"), QueryVal("guildId", "GuildId"));
            var guild = DiscordThreadService.ResolveGuild(services.Client, gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var senderName = FirstNonEmpty(
                DiscordThreadService.NormalizeDisplayName(BodyVal("DisplayName", "displayName"), BodyVal("DiscordUsername", "discordUsername")),
                BodyVal("UserName", "DriverName", "From", "Sender", "SenderName", "DispatchName", "userName", "driverName", "from", "sender", "senderName", "dispatchName"),
                QueryVal("displayName", "discordUsername", "userName", "driverName", "from", "sender", "senderName", "dispatchName"),
                "ELD");

            var routeToken = FirstNonEmpty(
                BodyVal("To", "Recipient", "Route", "Target", "Destination", "to", "recipient", "route", "target", "destination"),
                QueryVal("to", "recipient", "route", "target", "destination")
            );

            var routeToDispatch =
                string.Equals(routeToken, "dispatch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(routeToken, "dispatcher", StringComparison.OrdinalIgnoreCase);

            var payload = new SendMessageReq
            {
                GuildId = gidStr,
                Text = text,
                Body = BodyVal("Body", "body"),
                Message = BodyVal("Message", "message"),
                Content = BodyVal("Content", "content"),
                UserId = BodyVal("UserId", "userId"),
                DiscordUserId = BodyVal("DiscordUserId", "discordUserId"),
                DiscordUsername = BodyVal("DiscordUsername", "discordUsername"),
                DriverDiscordUserId = BodyVal("DriverDiscordUserId", "driverDiscordUserId"),
                Recipient = BodyVal("Recipient", "recipient"),
                To = BodyVal("To", "to"),
                DriverName = BodyVal("DriverName", "driverName"),
                DisplayName = BodyVal("DisplayName", "displayName"),
                Source = FirstNonEmpty(BodyVal("Source", "source"), QueryVal("source", "Source"))
            };

            ulong targetUserId = 0;

            if (routeToDispatch)
            {
                var senderIdStr = FirstNonEmpty(
                    BodyVal("UserId", "DiscordUserId", "DriverDiscordUserId", "userId", "discordUserId", "driverDiscordUserId"),
                    QueryVal("userId", "discordUserId", "driverDiscordUserId", "UserId", "DiscordUserId", "DriverDiscordUserId"));

                if (!ulong.TryParse(senderIdStr, out targetUserId) || targetUserId == 0)
                    targetUserId = await DiscordThreadService.ResolveTargetDriverUserIdAsync(guild, payload);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(payload.DriverDiscordUserId))
                    payload.DriverDiscordUserId = QueryVal("driverDiscordUserId", "DriverDiscordUserId");

                if (string.IsNullOrWhiteSpace(payload.Recipient))
                    payload.Recipient = QueryVal("recipient", "Recipient");

                if (string.IsNullOrWhiteSpace(payload.DriverName))
                    payload.DriverName = QueryVal("driverName", "DriverName");

                if (string.IsNullOrWhiteSpace(payload.DiscordUsername))
                    payload.DiscordUsername = QueryVal("discordUsername", "DiscordUsername");

                targetUserId = await DiscordThreadService.ResolveTargetDriverUserIdAsync(guild, payload);
            }

            if (targetUserId == 0)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "DriverTargetNotResolved",
                    hint = routeToDispatch
                        ? "Send UserId, DiscordUserId, or DriverDiscordUserId when routing to dispatch."
                        : "Send DriverDiscordUserId, recipient, driverName, or discordUsername."
                }, statusCode: 400);
            }

            var targetDisplay = DiscordThreadService.ResolveDriverDisplayName(guild, targetUserId, payload);
            var threadId = DiscordThreadService.ThreadStoreTryGet(services.ThreadStore, guild.Id, targetUserId);

            if (threadId == 0)
            {
                var created = await DiscordThreadService.EnsureDriverThreadAsync(
                    services.GuildSettingsStore,
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
                BodyVal("LoadNumber", "LoadNo", "CurrentLoadNumber", "loadNumber", "loadNo", "currentLoadNumber"),
                QueryVal("loadNumber", "loadNo", "currentLoadNumber", "LoadNumber", "LoadNo", "CurrentLoadNumber"));

            var truckId = FirstNonEmpty(
                BodyVal("TruckId", "TruckNumber", "AssignedTruck", "AssignedTruckId", "truckId", "truckNumber", "assignedTruck", "assignedTruckId"),
                QueryVal("truckId", "truckNumber", "assignedTruck", "assignedTruckId", "TruckId", "TruckNumber", "AssignedTruck", "AssignedTruckId"));

            var source = FirstNonEmpty(payload.Source, "eld");

            var prefixParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(loadNo)) prefixParts.Add($"Load {loadNo}");
            if (!string.IsNullOrWhiteSpace(truckId)) prefixParts.Add($"Truck {truckId}");
            if (!string.IsNullOrWhiteSpace(source)) prefixParts.Add(source);

            var prefix = prefixParts.Count == 0 ? "" : $"[{string.Join(" • ", prefixParts)}] ";
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
