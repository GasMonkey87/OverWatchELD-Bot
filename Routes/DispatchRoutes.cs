using System;
using System.Collections.Generic;
using System.Linq;
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

            return Results.Ok(new
            {
                ok = true,
                guildId,
                counts = new
                {
                    unassigned = loads.Count(x => x.Status == "unassigned"),
                    assigned = loads.Count(x => x.Status == "assigned"),
                    inTransit = loads.Count(x => x.Status == "in_transit"),
                    delivered = loads.Count(x => x.Status == "delivered"),
                    delayed = loads.Count(x => x.Status == "delayed")
                },
                staleDrivers = statuses.Count(x => (DateTimeOffset.UtcNow - x.LastSeenUtc).TotalMinutes >= 5)
            });
        });

        app.MapGet("/api/dispatch/loads", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;
            return Results.Ok(new { ok = true, guildId, rows = loadStore.List(guildId) });
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
                Commodity = ReadString(root, "commodity", "Commodity"),
                PickupLocation = ReadString(root, "pickupLocation", "PickupLocation", "origin", "Origin"),
                DropoffLocation = ReadString(root, "dropoffLocation", "DropoffLocation", "destination", "Destination"),
                DriverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId"),
                DriverName = ReadString(root, "driverName", "DriverName"),
                TruckId = ReadString(root, "truckId", "TruckId", "truck", "Truck"),
                DispatcherNotes = ReadString(root, "dispatcherNotes", "DispatcherNotes"),
                BolNumber = ReadString(root, "bolNumber", "BolNumber"),
                DueUtc = ReadDate(root, "dueUtc", "DueUtc")
            };

            if (string.IsNullOrWhiteSpace(load.LoadNumber))
                load.LoadNumber = $"LOAD-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (!string.IsNullOrWhiteSpace(load.DriverDiscordUserId))
                load.AssignedUtc = DateTimeOffset.UtcNow;

            load = loadStore.Upsert(load);
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
            existing.Commodity = DefaultIfBlank(ReadString(root, "commodity", "Commodity"), existing.Commodity);
            existing.PickupLocation = DefaultIfBlank(ReadString(root, "pickupLocation", "PickupLocation", "origin", "Origin"), existing.PickupLocation);
            existing.DropoffLocation = DefaultIfBlank(ReadString(root, "dropoffLocation", "DropoffLocation", "destination", "Destination"), existing.DropoffLocation);
            existing.DriverDiscordUserId = DefaultIfBlank(ReadString(root, "driverDiscordUserId", "DriverDiscordUserId"), existing.DriverDiscordUserId);
            existing.DriverName = DefaultIfBlank(ReadString(root, "driverName", "DriverName"), existing.DriverName);
            existing.TruckId = DefaultIfBlank(ReadString(root, "truckId", "TruckId", "truck", "Truck"), existing.TruckId);
            existing.DispatcherNotes = DefaultIfBlank(ReadString(root, "dispatcherNotes", "DispatcherNotes"), existing.DispatcherNotes);
            existing.BolNumber = DefaultIfBlank(ReadString(root, "bolNumber", "BolNumber"), existing.BolNumber);
            var maybeDue = ReadDate(root, "dueUtc", "DueUtc");
            if (maybeDue.HasValue) existing.DueUtc = maybeDue;

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

            messageStore.Add(new DispatchMessage
            {
                GuildId = guildId,
                DriverDiscordUserId = existing.DriverDiscordUserId,
                DriverName = existing.DriverName,
                Direction = "to_driver",
                Text = $"Load {existing.LoadNumber} assigned. Pickup: {existing.PickupLocation}. Dropoff: {existing.DropoffLocation}.",
                LoadNumber = existing.LoadNumber,
                IsRead = false
            });

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

            existing.Status = status;
            if (string.Equals(status, "in_transit", StringComparison.OrdinalIgnoreCase) && !existing.PickupUtc.HasValue)
                existing.PickupUtc = DateTimeOffset.UtcNow;
            if (string.Equals(status, "delivered", StringComparison.OrdinalIgnoreCase) && !existing.DeliveredUtc.HasValue)
                existing.DeliveredUtc = DateTimeOffset.UtcNow;

            existing = loadStore.Upsert(existing);
            return Results.Ok(new { ok = true, load = existing });
        });

        app.MapGet("/api/dispatch/conversations", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            var rows = messageStore.List(guildId)
                .GroupBy(x => new { x.DriverDiscordUserId, x.DriverName })
                .Select(g => new
                {
                    driverDiscordUserId = g.Key.DriverDiscordUserId,
                    driverName = g.Key.DriverName,
                    unread = g.Count(x => !x.IsRead && x.Direction == "from_driver"),
                    lastMessageUtc = g.Max(x => x.CreatedUtc),
                    lastText = g.OrderByDescending(x => x.CreatedUtc).FirstOrDefault()?.Text ?? ""
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

            return Results.Ok(new { ok = true, guildId, rows = messageStore.ListConversation(guildId, driverDiscordUserId) });
        });

        app.MapPost("/api/dispatch/messages/send", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId");
            var driverName = ReadString(root, "driverName", "DriverName");
            var text = ReadString(root, "text", "Text", "message", "Message");
            var loadNumber = ReadString(root, "loadNumber", "LoadNumber");

            if (string.IsNullOrWhiteSpace(driverDiscordUserId) || string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "MissingDriverOrText" }, statusCode: 400);

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

            return Results.Ok(new { ok = true, message = saved });
        });

        app.MapPost("/api/dispatch/messages/receive", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var guildId = ReadString(root, "guildId", "GuildId");
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId");
            var driverName = ReadString(root, "driverName", "DriverName");
            var text = ReadString(root, "text", "Text", "message", "Message");
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

            return Results.Ok(new { ok = true, message = saved });
        });

        app.MapPost("/api/dispatch/messages/mark-read", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireDispatchAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "DriverDiscordUserId");
            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.Json(new { ok = false, error = "MissingDriverDiscordUserId" }, statusCode: 400);

            var count = messageStore.MarkRead(guildId, driverDiscordUserId);
            return Results.Ok(new { ok = true, marked = count });
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

    private static IResult? RequireDispatchAccess(HttpContext ctx, string guildId)
    {
        if (string.IsNullOrWhiteSpace(guildId))
            return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);
        if (!AuthGuard.IsLoggedIn(ctx))
            return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);
        if (!AuthGuard.CanManageGuild(ctx, guildId))
            return Results.Json(new { ok = false, error = "Forbidden" }, statusCode: 403);
        return null;
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
}
