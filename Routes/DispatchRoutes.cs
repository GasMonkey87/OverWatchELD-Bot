using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class DispatchRoutes
{
    public static void Register(
        WebApplication app,
        BotServices services,
        JsonSerializerOptions jsonOpts,
        DispatchLoadStore loadStore,
        DispatchMessageStore messageStore)
    {
        app.MapGet("/api/dispatch/board", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var loads = loadStore.List(guildId);
            var statuses = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();
            var messages = messageStore.List(guildId);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                counts = new
                {
                    unassigned = loads.Count(x => Eq(x.Status, "unassigned")),
                    assigned = loads.Count(x => Eq(x.Status, "assigned")),
                    inTransit = loads.Count(x => Eq(x.Status, "in_transit")),
                    delivered = loads.Count(x => Eq(x.Status, "delivered")),
                    delayed = loads.Count(x => Eq(x.Status, "delayed")),
                    conversations = messages
                        .Where(x => !string.IsNullOrWhiteSpace(x.DriverDiscordUserId))
                        .Select(x => x.DriverDiscordUserId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    unreadMessages = messages.Count(x => !x.IsRead && Eq(x.Direction, "from_driver"))
                },
                staleDrivers = statuses.Count(x => (DateTimeOffset.UtcNow - x.LastSeenUtc).TotalMinutes >= 5)
            });
        });

        app.MapGet("/api/dispatch/loads", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var rows = loadStore.List(guildId)
                .OrderByDescending(x => x.UpdatedUtc)
                .ToList();

            return Results.Ok(new { ok = true, guildId, rows });
        });

        app.MapPost("/api/dispatch/loads/create", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var load = new DispatchLoad
            {
                GuildId = guildId,
                LoadNumber = ReadString(root, "loadNumber", "LoadNumber"),
                Status = DefaultIfBlank(ReadString(root, "status", "Status"), "unassigned"),
                Priority = DefaultIfBlank(ReadString(root, "priority", "Priority"), "normal"),
                Commodity = ReadString(root, "commodity", "Commodity", "cargo", "Cargo"),
                PickupLocation = ReadString(root, "pickupLocation", "PickupLocation", "origin", "Origin"),
                DropoffLocation = ReadString(root, "dropoffLocation", "DropoffLocation", "destination", "Destination"),
                DriverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId"),
                DriverName = ReadString(root, "driverName", "DriverName"),
                TruckId = ReadString(root, "truckId", "TruckId", "truck", "Truck"),
                DispatcherNotes = ReadString(root, "dispatcherNotes", "DispatcherNotes", "notes", "Notes"),
                BolNumber = ReadString(root, "bolNumber", "BolNumber"),
                DueUtc = ReadDate(root, "dueUtc", "DueUtc")
            };

            if (string.IsNullOrWhiteSpace(load.LoadNumber))
                load.LoadNumber = $"LOAD-{DateTime.UtcNow:yyyyMMddHHmmss}";

            if (!string.IsNullOrWhiteSpace(load.DriverDiscordUserId))
            {
                load.Status = Eq(load.Status, "unassigned") ? "assigned" : load.Status;
                load.AssignedUtc = DateTimeOffset.UtcNow;
            }

            load = loadStore.Upsert(load);

                        await MaybeAddSystemEventAsync(
                services,
                messageStore,
                guildId,
                load.DriverDiscordUserId,
                load.DriverName,
                load.LoadNumber,
                $"Load {load.LoadNumber} created.");

            return Results.Ok(new { ok = true, load });
        });

        app.MapPut("/api/dispatch/loads/update", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var id = ReadString(root, "id", "Id");
            if (string.IsNullOrWhiteSpace(id))
                return Results.Json(new { ok = false, error = "MissingId" }, statusCode: 400);

            var existing = loadStore.GetById(guildId, id);
            if (existing == null)
                return Results.Json(new { ok = false, error = "LoadNotFound" }, statusCode: 404);

            existing.LoadNumber = DefaultIfBlank(ReadString(root, "loadNumber", "LoadNumber"), existing.LoadNumber);
            existing.Status = DefaultIfBlank(ReadString(root, "status", "Status"), existing.Status);
            existing.Priority = DefaultIfBlank(ReadString(root, "priority", "Priority"), existing.Priority);
            existing.Commodity = DefaultIfBlank(ReadString(root, "commodity", "Commodity", "cargo", "Cargo"), existing.Commodity);
            existing.PickupLocation = DefaultIfBlank(ReadString(root, "pickupLocation", "PickupLocation", "origin", "Origin"), existing.PickupLocation);
            existing.DropoffLocation = DefaultIfBlank(ReadString(root, "dropoffLocation", "DropoffLocation", "destination", "Destination"), existing.DropoffLocation);
            existing.DriverDiscordUserId = DefaultIfBlank(ReadString(root, "driverDiscordUserId", "DriverDiscordUserId"), existing.DriverDiscordUserId);
            existing.DriverName = DefaultIfBlank(ReadString(root, "driverName", "DriverName"), existing.DriverName);
            existing.TruckId = DefaultIfBlank(ReadString(root, "truckId", "TruckId", "truck", "Truck"), existing.TruckId);
            existing.DispatcherNotes = DefaultIfBlank(ReadString(root, "dispatcherNotes", "DispatcherNotes", "notes", "Notes"), existing.DispatcherNotes);
            existing.BolNumber = DefaultIfBlank(ReadString(root, "bolNumber", "BolNumber"), existing.BolNumber);
            var maybeDue = ReadDate(root, "dueUtc", "DueUtc");
            if (maybeDue.HasValue)
                existing.DueUtc = maybeDue;

            existing = loadStore.Upsert(existing);
            return Results.Ok(new { ok = true, load = existing });
        });

        app.MapPost("/api/dispatch/loads/assign", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var id = ReadString(root, "id", "Id");
            var existing = loadStore.GetById(guildId, id);
            if (existing == null)
                return Results.Json(new { ok = false, error = "LoadNotFound" }, statusCode: 404);

            existing.DriverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId");
            existing.DriverName = ReadString(root, "driverName", "DriverName");
            existing.TruckId = ReadString(root, "truckId", "TruckId", "truck", "Truck");
            existing.Status = "assigned";
            existing.AssignedUtc = DateTimeOffset.UtcNow;
            existing = loadStore.Upsert(existing);

            if (!string.IsNullOrWhiteSpace(existing.DriverDiscordUserId))
            {
                var assignedMessage = messageStore.Add(new DispatchMessage
                {
                    GuildId = guildId,
                    DriverDiscordUserId = existing.DriverDiscordUserId,
                    DriverName = existing.DriverName,
                    Direction = "to_driver",
                    Text = $"Load {existing.LoadNumber} assigned. Pickup: {existing.PickupLocation}. Dropoff: {existing.DropoffLocation}.",
                    LoadNumber = existing.LoadNumber,
                    IsRead = false
                });

                await TrySyncDispatchMessageToDiscordAsync(services, guildId, assignedMessage, "Load Assigned");
            }

            return Results.Ok(new { ok = true, load = existing });
        });

        app.MapPost("/api/dispatch/loads/status", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var id = ReadString(root, "id", "Id");
            var status = ReadString(root, "status", "Status");
            var existing = loadStore.GetById(guildId, id);
            if (existing == null)
                return Results.Json(new { ok = false, error = "LoadNotFound" }, statusCode: 404);

            if (string.IsNullOrWhiteSpace(status))
                return Results.Json(new { ok = false, error = "MissingStatus" }, statusCode: 400);

            var previousStatus = existing.Status ?? "";
            existing.Status = status;
            if ((Eq(status, "in_transit") || Eq(status, "picked_up") || Eq(status, "pickedup")) && !existing.PickupUtc.HasValue)
                existing.PickupUtc = DateTimeOffset.UtcNow;
            if (Eq(status, "delivered") && !existing.DeliveredUtc.HasValue)
                existing.DeliveredUtc = DateTimeOffset.UtcNow;

            if ((Eq(status, "in_transit") || Eq(status, "picked_up") || Eq(status, "pickedup")) && string.IsNullOrWhiteSpace(existing.BolNumber))
            {
                existing.BolNumber = await EnsureAutoBolAsync(existing);
            }

            if (Eq(status, "delivered") && !string.IsNullOrWhiteSpace(existing.BolNumber))
            {
                await MarkAutoBolDeliveredAsync(existing);
            }

            existing = loadStore.Upsert(existing);

            var statusEventText = BuildLoadStatusEventText(existing.LoadNumber, previousStatus, status, existing.BolNumber);

            await MaybeAddSystemEventAsync(
                services,
                messageStore,
                guildId,
                existing.DriverDiscordUserId,
                existing.DriverName,
                existing.LoadNumber,
                statusEventText);

            return Results.Ok(new { ok = true, load = existing });
        });

        app.MapGet("/api/dispatch/conversations", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var loads = loadStore.List(guildId);
            var rows = messageStore.List(guildId)
                .Where(x => !string.IsNullOrWhiteSpace(x.DriverDiscordUserId))
                .GroupBy(x => new { x.DriverDiscordUserId, x.DriverName })
                .Select(g =>
                {
                    var ordered = g.OrderByDescending(x => x.CreatedUtc).ToList();
                    var last = ordered.FirstOrDefault();
                    var currentLoad = loads.FirstOrDefault(x =>
                        string.Equals(x.DriverDiscordUserId, g.Key.DriverDiscordUserId, StringComparison.OrdinalIgnoreCase) &&
                        !Eq(x.Status, "delivered"));

                    return new
                    {
                        driverDiscordUserId = g.Key.DriverDiscordUserId,
                        driverName = g.Key.DriverName,
                        unread = g.Count(x => !x.IsRead && Eq(x.Direction, "from_driver")),
                        totalMessages = g.Count(),
                        lastMessageUtc = last?.CreatedUtc,
                        lastText = last?.Text ?? "",
                        lastDirection = last?.Direction ?? "",
                        activeLoadNumber = currentLoad?.LoadNumber ?? "",
                        activeLoadStatus = currentLoad?.Status ?? ""
                    };
                })
                .OrderByDescending(x => x.lastMessageUtc)
                .ToList();

            return Results.Ok(new { ok = true, guildId, rows });
        });

        app.MapGet("/api/dispatch/conversations/{driverDiscordUserId}", (HttpContext ctx, string driverDiscordUserId, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var rows = messageStore.ListConversation(guildId, driverDiscordUserId);
            var activeLoads = loadStore.List(guildId)
                .Where(x => string.Equals(x.DriverDiscordUserId, driverDiscordUserId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedUtc)
                .ToList();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                driverDiscordUserId,
                rows,
                activeLoads
            });
        });

        app.MapGet("/api/dispatch/conversations/{driverDiscordUserId}/summary", (HttpContext ctx, string driverDiscordUserId, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var conversation = messageStore.ListConversation(guildId, driverDiscordUserId);
            var last = conversation.OrderByDescending(x => x.CreatedUtc).FirstOrDefault();
            var unread = conversation.Count(x => !x.IsRead && Eq(x.Direction, "from_driver"));
            var load = loadStore.List(guildId)
                .FirstOrDefault(x => string.Equals(x.DriverDiscordUserId, driverDiscordUserId, StringComparison.OrdinalIgnoreCase) && !Eq(x.Status, "delivered"));

            return Results.Ok(new
            {
                ok = true,
                guildId,
                driverDiscordUserId,
                unread,
                lastMessageUtc = last?.CreatedUtc,
                lastText = last?.Text ?? "",
                activeLoad = load
            });
        });

        app.MapPost("/api/dispatch/messages/send", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId", "driverId", "DriverId");
            var driverName = ReadString(root, "driverName", "DriverName");
            var text = ReadString(root, "text", "Text", "message", "Message", "body", "Body", "content", "Content");
            var loadNumber = ReadString(root, "loadNumber", "LoadNumber");

            if (string.IsNullOrWhiteSpace(driverDiscordUserId) || string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "MissingDriverOrText", hint = "Expected driverDiscordUserId and text/body/message/content." }, statusCode: 400);

            var saved = messageStore.Add(new DispatchMessage
            {
                GuildId = guildId,
                DriverDiscordUserId = driverDiscordUserId,
                DriverName = driverName,
                Direction = "to_driver",
                Text = text,
                LoadNumber = loadNumber,
                IsRead = false
            });

            await TrySyncDispatchMessageToDiscordAsync(services, guildId, saved, "Dispatch Message");
            return Results.Ok(new { ok = true, message = saved });
        });

        app.MapPost("/api/dispatch/send", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId", "driverId", "DriverId");
            var driverName = ReadString(root, "driverName", "DriverName");
            var text = ReadString(root, "text", "Text", "message", "Message", "body", "Body", "content", "Content");
            var loadNumber = ReadString(root, "loadNumber", "LoadNumber");

            if (string.IsNullOrWhiteSpace(driverDiscordUserId) || string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "MissingDriverOrText", hint = "Expected driverDiscordUserId and text/body/message/content." }, statusCode: 400);

            var saved = messageStore.Add(new DispatchMessage
            {
                GuildId = guildId,
                DriverDiscordUserId = driverDiscordUserId,
                DriverName = driverName,
                Direction = "to_driver",
                Text = text,
                LoadNumber = loadNumber,
                IsRead = false
            });

            await TrySyncDispatchMessageToDiscordAsync(services, guildId, saved, "Dispatch Message");
            return Results.Ok(new { ok = true, message = saved });
        });

        app.MapPost("/api/dispatch/messages/receive", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var guildId = ReadString(root, "guildId", "GuildId");
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId", "driverId", "DriverId");
            var driverName = ReadString(root, "driverName", "DriverName");
            var text = ReadString(root, "text", "Text", "message", "Message", "body", "Body", "content", "Content");
            var loadNumber = ReadString(root, "loadNumber", "LoadNumber");

            if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(driverDiscordUserId) || string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "MissingGuildIdDriverOrText" }, statusCode: 400);

            var saved = messageStore.Add(new DispatchMessage
            {
                GuildId = guildId,
                DriverDiscordUserId = driverDiscordUserId,
                DriverName = driverName,
                Direction = "from_driver",
                Text = text,
                LoadNumber = loadNumber,
                IsRead = false
            });

            await TrySyncDispatchMessageToDiscordAsync(services, guildId, saved, "Driver Reply");
            return Results.Ok(new { ok = true, message = saved });
        });

        app.MapPost("/api/dispatch/messages/mark-read", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId", "driverId", "DriverId");
            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.Json(new { ok = false, error = "MissingDriverDiscordUserId" }, statusCode: 400);

            var count = messageStore.MarkRead(guildId, driverDiscordUserId);
            return Results.Ok(new { ok = true, marked = count });
        });

        app.MapPost("/api/dispatch/mark-read", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId", "driverId", "DriverId");
            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.Json(new { ok = false, error = "MissingDriverDiscordUserId" }, statusCode: 400);

            var count = messageStore.MarkRead(guildId, driverDiscordUserId);
            return Results.Ok(new { ok = true, marked = count });
        });

        app.MapGet("/api/dispatch/unread-count", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var unread = messageStore.List(guildId).Count(x => !x.IsRead && Eq(x.Direction, "from_driver"));
            return Results.Ok(new { ok = true, guildId, unread });
        });

        app.MapGet("/api/dispatch/drivers/live", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var rows = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();
            return Results.Ok(new { ok = true, guildId, rows });
        });
    }

    private static async Task MaybeAddSystemEventAsync(
        BotServices services,
        DispatchMessageStore messageStore,
        string guildId,
        string driverDiscordUserId,
        string driverName,
        string loadNumber,
        string text)
    {
        if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(driverDiscordUserId) || string.IsNullOrWhiteSpace(text))
            return;

        var saved = messageStore.Add(new DispatchMessage
        {
            GuildId = guildId,
            DriverDiscordUserId = driverDiscordUserId,
            DriverName = driverName,
            Direction = "system",
            Text = text,
            LoadNumber = loadNumber,
            IsRead = true,
            CreatedUtc = DateTimeOffset.UtcNow
        });

        await TrySyncDispatchMessageToDiscordAsync(services, guildId, saved, "System Event");
    }

    private static async Task TrySyncDispatchMessageToDiscordAsync(BotServices services, string guildId, DispatchMessage message, string reason)
    {
        try
        {
            if (services?.Client == null || message == null || string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(message.DriverDiscordUserId))
                return;

            var settings = ReadDispatchSyncSettings(guildId);
            if (settings.DispatchChannelId == 0)
                return;

            var target = await GetOrCreateDispatchTargetAsync(services.Client, guildId, settings, message);
            if (target == null)
                return;

            var content = FormatDiscordDispatchMessage(message, reason);
            await InvokeOptionalAsync(target, "SendMessageAsync", content);
        }
        catch
        {
            // Intentionally swallow sync errors so dispatch storage never fails because Discord sync failed.
        }
    }

    private static DispatchSyncSettings ReadDispatchSyncSettings(string guildId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", $"settings_{guildId}.json");
        if (!File.Exists(path))
            return new DispatchSyncSettings();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        return new DispatchSyncSettings
        {
            DispatchChannelId = ReadUInt64(root, "dispatchChannelId", "DispatchChannelId")
                ?? ReadNestedUInt64(root, "discord", "dispatchChannelId")
                ?? ReadNestedUInt64(root, "Discord", "DispatchChannelId")
                ?? 0,
            UseThreadsPerDriver = ReadBool(root, "useThreadsPerDriver", "UseThreadsPerDriver")
                ?? ReadNestedBool(root, "discord", "useThreadsPerDriver")
                ?? ReadNestedBool(root, "Discord", "UseThreadsPerDriver")
                ?? true
        };
    }

    private static async Task<object?> GetOrCreateDispatchTargetAsync(object client, string guildId, DispatchSyncSettings settings, DispatchMessage message)
    {
        if (settings.UseThreadsPerDriver)
        {
            var threadMap = ReadThreadMap(guildId);
            if (threadMap.TryGetValue(message.DriverDiscordUserId ?? "", out var existingThreadId) && existingThreadId != 0)
            {
                var existingThread = GetClientChannel(client, existingThreadId);
                if (existingThread != null)
                    return existingThread;
            }
        }

        var guild = GetGuild(client, guildId);
        if (guild == null)
            return null;

        var dispatchChannel = GetTextChannel(guild, settings.DispatchChannelId);
        if (dispatchChannel == null)
            return null;

        if (!settings.UseThreadsPerDriver)
            return dispatchChannel;

        var threadName = BuildDispatchThreadName(message);
        var createdThread = await CreateThreadIfPossibleAsync(dispatchChannel, threadName);
        if (createdThread == null)
            return dispatchChannel;

        var threadId = GetSnowflakeId(createdThread);
        if (threadId != 0)
        {
            var threadMap = ReadThreadMap(guildId);
            threadMap[message.DriverDiscordUserId ?? ""] = threadId;
            WriteThreadMap(guildId, threadMap);
        }

        return createdThread;
    }

    private static string FormatDiscordDispatchMessage(DispatchMessage message, string reason)
    {
        var who = Eq(message.Direction, "from_driver") ? "Driver" : Eq(message.Direction, "system") ? "System" : "Dispatch";
        var load = string.IsNullOrWhiteSpace(message.LoadNumber) ? "" : $"\nLoad: {message.LoadNumber}";
        var driver = string.IsNullOrWhiteSpace(message.DriverName) ? message.DriverDiscordUserId ?? "Unknown Driver" : message.DriverName;
        return $"[{reason}] {who}: {driver}{load}\n{message.Text}";
    }

    private static string BuildDispatchThreadName(DispatchMessage message)
    {
        var driver = string.IsNullOrWhiteSpace(message.DriverName) ? "driver" : message.DriverName.Trim();
        if (!string.IsNullOrWhiteSpace(message.LoadNumber))
            return $"dispatch-{driver}-{message.LoadNumber}";
        return $"dispatch-{driver}";
    }

    private static object? GetGuild(object client, string guildId)
    {
        if (!ulong.TryParse(guildId, out var guildSnowflake))
            return null;

        var direct = client.GetType().GetMethod("GetGuild", BindingFlags.Instance | BindingFlags.Public);
        if (direct != null)
        {
            try
            {
                var guild = direct.Invoke(client, new object[] { guildSnowflake });
                if (guild != null)
                    return guild;
            }
            catch { }
        }

        var guildsProp = client.GetType().GetProperty("Guilds", BindingFlags.Instance | BindingFlags.Public);
        if (guildsProp?.GetValue(client) is System.Collections.IEnumerable guilds)
        {
            foreach (var guild in guilds)
            {
                if (GetSnowflakeId(guild) == guildSnowflake)
                    return guild;
            }
        }

        return null;
    }

    private static object? GetTextChannel(object guild, ulong channelId)
    {
        if (channelId == 0)
            return null;

        var direct = guild.GetType().GetMethod("GetTextChannel", BindingFlags.Instance | BindingFlags.Public);
        if (direct != null)
        {
            try
            {
                var channel = direct.Invoke(guild, new object[] { channelId });
                if (channel != null)
                    return channel;
            }
            catch { }
        }

        foreach (var propName in new[] { "TextChannels", "Channels" })
        {
            var prop = guild.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (prop?.GetValue(guild) is System.Collections.IEnumerable channels)
            {
                foreach (var channel in channels)
                {
                    if (GetSnowflakeId(channel) == channelId)
                        return channel;
                }
            }
        }

        return null;
    }

    private static object? GetClientChannel(object client, ulong channelId)
    {
        if (channelId == 0)
            return null;

        var direct = client.GetType().GetMethod("GetChannel", BindingFlags.Instance | BindingFlags.Public);
        if (direct != null)
        {
            try
            {
                return direct.Invoke(client, new object[] { channelId });
            }
            catch { }
        }

        return null;
    }

    private static async Task<object?> CreateThreadIfPossibleAsync(object dispatchChannel, string threadName)
    {
        var createMethod = dispatchChannel.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => string.Equals(m.Name, "CreateThreadAsync", StringComparison.Ordinal) && m.GetParameters().Length >= 1);

        if (createMethod == null)
            return null;

        var parameters = createMethod.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = threadName;
        for (var i = 1; i < parameters.Length; i++)
            args[i] = Type.Missing;

        try
        {
            var result = createMethod.Invoke(dispatchChannel, args);
            return await UnwrapTaskResultAsync(result);
        }
        catch
        {
            return null;
        }
    }

    private static async Task InvokeOptionalAsync(object target, string methodName, string content)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.Ordinal) && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
        if (method == null)
            return;

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = content;
        for (var i = 1; i < parameters.Length; i++)
            args[i] = Type.Missing;

        try
        {
            var task = method.Invoke(target, args);
            await UnwrapTaskResultAsync(task);
        }
        catch
        {
        }
    }

    private static async Task<object?> UnwrapTaskResultAsync(object? possibleTask)
    {
        if (possibleTask is not Task task)
            return possibleTask;

        await task.ConfigureAwait(false);
        var type = task.GetType();
        if (!type.IsGenericType)
            return null;

        var resultProp = type.GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
        return resultProp?.GetValue(task);
    }

    private static Dictionary<string, ulong> ReadThreadMap(string guildId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", $"dispatch_threads_{guildId}.json");
        if (!File.Exists(path))
            return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(path))
                ?? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteThreadMap(string guildId, Dictionary<string, ulong> map)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", $"dispatch_threads_{guildId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.Combine(AppContext.BaseDirectory, "data"));
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ulong GetSnowflakeId(object? value)
    {
        if (value == null)
            return 0;

        var prop = value.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
        var raw = prop?.GetValue(value);
        if (raw is ulong ul)
            return ul;
        if (raw != null && ulong.TryParse(raw.ToString(), out var parsed))
            return parsed;
        return 0;
    }

    private static ulong? ReadUInt64(JsonElement root, params string[] names)
    {
        var raw = ReadString(root, names);
        return ulong.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static ulong? ReadNestedUInt64(JsonElement root, string parentName, string childName)
    {
        if (!root.TryGetProperty(parentName, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return null;
        var raw = ReadString(parent, childName);
        return ulong.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static bool? ReadBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var p))
                continue;
            if (p.ValueKind == JsonValueKind.True)
                return true;
            if (p.ValueKind == JsonValueKind.False)
                return false;
            if (bool.TryParse(p.ToString(), out var parsed))
                return parsed;
        }
        return null;
    }

    private static bool? ReadNestedBool(JsonElement root, string parentName, string childName)
    {
        if (!root.TryGetProperty(parentName, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return null;
        return ReadBool(parent, childName);
    }


    private static async Task<string> EnsureAutoBolAsync(DispatchLoad load)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        var path = Path.Combine(dataDir, $"bol_{load.GuildId}.json");
        var list = new List<Dictionary<string, object>>();

        if (File.Exists(path))
        {
            var existingJson = await File.ReadAllTextAsync(path);
            list = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(existingJson) ?? new List<Dictionary<string, object>>();
        }

        if (!string.IsNullOrWhiteSpace(load.BolNumber))
        {
            var existingBol = list.FirstOrDefault(x =>
                TryGetValue(x, "bolId", out var bolId) &&
                string.Equals(bolId, load.BolNumber, StringComparison.OrdinalIgnoreCase));

            if (existingBol != null)
                return load.BolNumber!;
        }

        var matchedByLoad = list.FirstOrDefault(x =>
            TryGetValue(x, "loadNumber", out var loadNumber) &&
            string.Equals(loadNumber, load.LoadNumber, StringComparison.OrdinalIgnoreCase));

        if (matchedByLoad != null && TryGetValue(matchedByLoad, "bolId", out var matchedBolId) && !string.IsNullOrWhiteSpace(matchedBolId))
            return matchedBolId;

        var bolIdValue = $"BOL-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["bolId"] = bolIdValue,
            ["guildId"] = load.GuildId ?? "",
            ["loadNumber"] = load.LoadNumber ?? "",
            ["driverDiscordUserId"] = load.DriverDiscordUserId ?? "",
            ["driverName"] = load.DriverName ?? "",
            ["truckId"] = load.TruckId ?? "",
            ["cargo"] = load.Commodity ?? "",
            ["commodity"] = load.Commodity ?? "",
            ["origin"] = load.PickupLocation ?? "",
            ["destination"] = load.DropoffLocation ?? "",
            ["status"] = "InTransit",
            ["createdUtc"] = DateTime.UtcNow,
            ["pickupUtc"] = load.PickupUtc?.UtcDateTime ?? DateTime.UtcNow
        };

        list.Insert(0, row);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        return bolIdValue;
    }

    private static async Task MarkAutoBolDeliveredAsync(DispatchLoad load)
    {
        if (string.IsNullOrWhiteSpace(load.GuildId))
            return;

        var path = Path.Combine(AppContext.BaseDirectory, "data", $"bol_{load.GuildId}.json");
        if (!File.Exists(path))
            return;

        var list = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(await File.ReadAllTextAsync(path)) ?? new List<Dictionary<string, object>>();
        Dictionary<string, object>? matched = null;

        if (!string.IsNullOrWhiteSpace(load.BolNumber))
        {
            matched = list.FirstOrDefault(x =>
                TryGetValue(x, "bolId", out var bolId) &&
                string.Equals(bolId, load.BolNumber, StringComparison.OrdinalIgnoreCase));
        }

        matched ??= list.FirstOrDefault(x =>
            TryGetValue(x, "loadNumber", out var loadNumber) &&
            string.Equals(loadNumber, load.LoadNumber, StringComparison.OrdinalIgnoreCase));

        if (matched == null)
            return;

        matched["status"] = "Delivered";
        matched["deliveredUtc"] = DateTime.UtcNow;

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string BuildLoadStatusEventText(string? loadNumber, string? previousStatus, string? currentStatus, string? bolNumber)
    {
        var loadLabel = string.IsNullOrWhiteSpace(loadNumber) ? "Load" : $"Load {loadNumber}";
        var current = NormalizeStatus(currentStatus);

        if (Eq(currentStatus, "assigned"))
            return $"{loadLabel} assigned to driver.";

        if (Eq(currentStatus, "picked_up") || Eq(currentStatus, "pickedup"))
            return string.IsNullOrWhiteSpace(bolNumber)
                ? $"{loadLabel} picked up."
                : $"{loadLabel} picked up. Auto-created BOL {bolNumber}.";

        if (Eq(currentStatus, "in_transit"))
            return string.IsNullOrWhiteSpace(bolNumber)
                ? $"{loadLabel} is now in transit."
                : $"{loadLabel} is now in transit. Auto-created BOL {bolNumber}.";

        if (Eq(currentStatus, "delivered"))
            return string.IsNullOrWhiteSpace(bolNumber)
                ? $"{loadLabel} delivered."
                : $"{loadLabel} delivered. Marked BOL {bolNumber} delivered.";

        if (!string.IsNullOrWhiteSpace(previousStatus) && !Eq(previousStatus, currentStatus))
            return $"{loadLabel} status changed from {NormalizeStatus(previousStatus)} to {current}.";

        return $"{loadLabel} status updated to {current}.";
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "Unknown";

        var value = status.Replace("_", " ").Trim().ToLowerInvariant();
        return string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }

    private static bool TryGetValue(Dictionary<string, object> row, string key, out string value)
    {
        value = "";
        if (!row.TryGetValue(key, out var raw) || raw == null)
            return false;

        value = raw.ToString()?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed class DispatchSyncSettings
    {
        public ulong DispatchChannelId { get; set; }
        public bool UseThreadsPerDriver { get; set; } = true;
    }

    private static IResult? RequireDispatchAccess(HttpContext ctx, string guildId)
    {
        if (string.IsNullOrWhiteSpace(guildId))
            return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

        if (IsTrustedApiRequest(ctx.Request))
            return null;

        if (!AuthGuard.IsLoggedIn(ctx))
            return Results.Json(new { ok = false, error = "Unauthorized", hint = "Log into the web dashboard or send a valid API key header." }, statusCode: 401);

        if (!AuthGuard.CanManageGuild(ctx, guildId))
            return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);

        return null;
    }

    private static bool IsTrustedApiRequest(HttpRequest req)
    {
        var configuredKeys = GetConfiguredApiKeys();
        if (configuredKeys.Count == 0)
            return false;

        var candidates = new List<string?>
        {
            req.Headers["X-API-Key"].FirstOrDefault(),
            req.Headers["X-OverWatch-Key"].FirstOrDefault(),
            req.Headers["X-Dispatch-Key"].FirstOrDefault(),
            req.Query["apiKey"].ToString()
        };

        var authHeader = req.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            const string bearer = "Bearer ";
            const string keyPrefix = "Key ";
            if (authHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                candidates.Add(authHeader[bearer.Length..].Trim());
            else if (authHeader.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                candidates.Add(authHeader[keyPrefix.Length..].Trim());
            else
                candidates.Add(authHeader.Trim());
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            foreach (var configured in configuredKeys)
            {
                if (string.Equals(candidate.Trim(), configured, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static List<string> GetConfiguredApiKeys()
    {
        var values = new[]
        {
            Environment.GetEnvironmentVariable("MANAGEMENT_API_KEY"),
            Environment.GetEnvironmentVariable("DISPATCH_API_KEY"),
            Environment.GetEnvironmentVariable("OVERWATCH_API_KEY"),
            Environment.GetEnvironmentVariable("BOT_API_KEY"),
            Environment.GetEnvironmentVariable("INTERNAL_API_KEY")
        };

        return values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string ResolveGuildId(HttpRequest req, BotServices services)
    {
        var guildId = (req.Query["guildId"].ToString() ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(guildId))
            return guildId;
        return services.Client?.Guilds.FirstOrDefault()?.Id.ToString() ?? "";
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

    private static DateTimeOffset? ReadDate(JsonElement root, params string[] names)
    {
        var raw = ReadString(root, names);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(raw, out var dto) ? dto : null;
    }

    private static string DefaultIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static bool Eq(string? left, string right)
        => string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);
}
