using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class ManagementRoutes
{
    public static void Register(
        WebApplication app,
        BotServices services,
        DispatchMessageStore messageStore)
    {
        app.MapGet("/api/management/drivers", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireManagementAccess(ctx, guildId);
            if (auth != null) return auth;

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var statusRows = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();
            var convoRows = messageStore.List(guildId);

            var drivers = roster
                .Select(r =>
                {
                    var live = statusRows.FirstOrDefault(x =>
                        string.Equals((x.DiscordUserId ?? "").Trim(), (r.DiscordUserId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

                    var driverMsgs = convoRows
                        .Where(x => string.Equals((x.DriverDiscordUserId ?? "").Trim(), (r.DiscordUserId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var unread = driverMsgs.Count(x =>
                        string.Equals((x.Direction ?? "").Trim(), "from_driver", StringComparison.OrdinalIgnoreCase) &&
                        !x.IsRead);

                    var lastMsg = driverMsgs
                        .OrderByDescending(x => x.CreatedUtc)
                        .FirstOrDefault();

                    return new
                    {
                        driverId = r.DriverId ?? "",
                        name = r.Name ?? "",
                        driverName = r.Name ?? "",
                        discordUserId = r.DiscordUserId ?? "",
                        discordUsername = r.DiscordUsername ?? "",
                        truckNumber = r.TruckNumber ?? "",
                        role = string.IsNullOrWhiteSpace(r.Role) ? "Driver" : r.Role,
                        status = r.Status ?? "",
                        notes = r.Notes ?? "",

                        dutyStatus = live?.DutyStatus ?? "",
                        location = live?.Location ?? "",
                        loadNumber = live?.LoadNumber ?? "",
                        speedMph = live?.SpeedMph ?? 0,
                        lastSeenUtc = live?.LastSeenUtc,

                        unreadDispatchCount = unread,
                        lastMessageUtc = lastMsg?.CreatedUtc,
                        lastMessageText = lastMsg?.Text ?? ""
                    };
                })
                .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(new
            {
                ok = true,
                guildId,
                drivers
            });
        });

        app.MapGet("/api/management/drivers/{driverDiscordUserId}", (HttpContext ctx, string driverDiscordUserId, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireManagementAccess(ctx, guildId);
            if (auth != null) return auth;

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var row = roster.FirstOrDefault(x =>
                string.Equals((x.DiscordUserId ?? "").Trim(), (driverDiscordUserId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

            if (row == null)
                return Results.Json(new { ok = false, error = "DriverNotFound" }, statusCode: 404);

            var live = services.DriverStatusStore?.List(guildId)
                .FirstOrDefault(x =>
                    string.Equals((x.DiscordUserId ?? "").Trim(), (driverDiscordUserId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

            var messages = messageStore.ListConversation(guildId, driverDiscordUserId);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                driver = new
                {
                    driverId = row.DriverId ?? "",
                    name = row.Name ?? "",
                    driverName = row.Name ?? "",
                    discordUserId = row.DiscordUserId ?? "",
                    discordUsername = row.DiscordUsername ?? "",
                    truckNumber = row.TruckNumber ?? "",
                    role = string.IsNullOrWhiteSpace(row.Role) ? "Driver" : row.Role,
                    status = row.Status ?? "",
                    notes = row.Notes ?? "",
                    createdUtc = row.CreatedUtc,
                    updatedUtc = row.UpdatedUtc
                },
                live = live == null ? null : new
                {
                    dutyStatus = live.DutyStatus ?? "",
                    truck = live.Truck ?? "",
                    loadNumber = live.LoadNumber ?? "",
                    location = live.Location ?? "",
                    speedMph = live.SpeedMph,
                    latitude = live.Latitude,
                    longitude = live.Longitude,
                    heading = live.Heading,
                    lastSeenUtc = live.LastSeenUtc
                },
                messages
            });
        });

        app.MapPost("/api/management/driver/role", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireManagementAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "discordUserId", "DriverDiscordUserId", "DiscordUserId");
            var role = ReadString(root, "role", "Role");

            if (string.IsNullOrWhiteSpace(driverDiscordUserId) || string.IsNullOrWhiteSpace(role))
                return Results.Json(new { ok = false, error = "MissingDriverOrRole" }, statusCode: 400);

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var driver = roster.FirstOrDefault(x =>
                string.Equals((x.DiscordUserId ?? "").Trim(), driverDiscordUserId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (driver == null)
                return Results.Json(new { ok = false, error = "DriverNotFound" }, statusCode: 404);

            driver.Role = role.Trim();
            TouchUpdatedUtc(driver);

            var persisted = TryPersistRoster(services.RosterStore, roster, driver);

            return Results.Ok(new
            {
                ok = true,
                persisted,
                driverDiscordUserId = driver.DiscordUserId ?? "",
                role = driver.Role ?? ""
            });
        });

        app.MapPost("/api/management/driver/note", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireManagementAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var driverDiscordUserId = ReadString(root, "driverDiscordUserId", "discordUserId", "DriverDiscordUserId", "DiscordUserId");
            var notes = ReadString(root, "notes", "Notes");

            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                return Results.Json(new { ok = false, error = "MissingDriverDiscordUserId" }, statusCode: 400);

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var driver = roster.FirstOrDefault(x =>
                string.Equals((x.DiscordUserId ?? "").Trim(), driverDiscordUserId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (driver == null)
                return Results.Json(new { ok = false, error = "DriverNotFound" }, statusCode: 404);

            driver.Notes = notes ?? "";
            TouchUpdatedUtc(driver);

            var persisted = TryPersistRoster(services.RosterStore, roster, driver);

            return Results.Ok(new
            {
                ok = true,
                persisted,
                driverDiscordUserId = driver.DiscordUserId ?? "",
                notes = driver.Notes ?? ""
            });
        });

        app.MapPost("/api/management/message/fleet", async (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireManagementAccess(ctx, guildId);
            if (auth != null) return auth;

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var text = ReadString(root, "text", "Text", "message", "Message", "body", "Body", "content", "Content");
            var loadNumber = ReadString(root, "loadNumber", "LoadNumber");

            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "MissingText" }, statusCode: 400);

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();

            var sent = 0;
            foreach (var driver in roster)
            {
                if (string.IsNullOrWhiteSpace(driver.DiscordUserId))
                    continue;

                messageStore.Add(new DispatchMessage
                {
                    GuildId = guildId,
                    DriverDiscordUserId = driver.DiscordUserId ?? "",
                    DriverName = driver.Name ?? "",
                    Direction = "to_driver",
                    Text = text.Trim(),
                    LoadNumber = loadNumber ?? "",
                    IsRead = false
                });

                sent++;
            }

            return Results.Ok(new
            {
                ok = true,
                guildId,
                sent
            });
        });

        app.MapGet("/api/management/summary", (HttpContext ctx, HttpRequest req) =>
        {
            var guildId = ResolveGuildId(req, services);
            var auth = RequireManagementAccess(ctx, guildId);
            if (auth != null) return auth;

            var roster = services.RosterStore?.List(guildId) ?? new List<VtcDriver>();
            var live = services.DriverStatusStore?.List(guildId) ?? new List<DriverStatusStore.DriverStatusEntry>();
            var msgs = messageStore.List(guildId);

            var unread = msgs.Count(x =>
                string.Equals((x.Direction ?? "").Trim(), "from_driver", StringComparison.OrdinalIgnoreCase) &&
                !x.IsRead);

            var staleCount = live.Count(x => (DateTimeOffset.UtcNow - x.LastSeenUtc).TotalMinutes >= 5);

            return Results.Ok(new
            {
                ok = true,
                guildId,
                counts = new
                {
                    drivers = roster.Count,
                    live = live.Count,
                    stale = staleCount,
                    unreadDispatch = unread,
                    managers = roster.Count(x => IsManagerLike(x.Role)),
                    dispatchers = roster.Count(x => IsDispatchLike(x.Role))
                }
            });
        });
    }

    private static IResult? RequireManagementAccess(HttpContext ctx, string guildId)
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

    private static bool TryPersistRoster(VtcRosterStore? store, List<VtcDriver> roster, VtcDriver changedDriver)
    {
        if (store == null)
            return false;

        var type = store.GetType();

        try
        {
            var upsert = type.GetMethod("Upsert", BindingFlags.Public | BindingFlags.Instance);
            if (upsert != null)
            {
                upsert.Invoke(store, new object[] { changedDriver });
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var save = type.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
            if (save != null)
            {
                save.Invoke(store, new object[] { roster });
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var replaceAll = type.GetMethod("ReplaceAll", BindingFlags.Public | BindingFlags.Instance);
            if (replaceAll != null)
            {
                replaceAll.Invoke(store, new object[] { roster });
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var setAll = type.GetMethod("SetAll", BindingFlags.Public | BindingFlags.Instance);
            if (setAll != null)
            {
                setAll.Invoke(store, new object[] { roster });
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static void TouchUpdatedUtc(VtcDriver driver)
    {
        try
        {
            driver.UpdatedUtc = DateTimeOffset.UtcNow;
        }
        catch
        {
        }
    }

    private static bool IsManagerLike(string? role)
    {
        var v = (role ?? "").Trim();
        return v.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Supervisor", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Fleet Manager", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Roster Manager", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDispatchLike(string? role)
    {
        var v = (role ?? "").Trim();
        return v.Equals("Dispatch", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Dispatch Admin", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("Owner", StringComparison.OrdinalIgnoreCase);
    }
}
