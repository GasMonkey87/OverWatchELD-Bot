using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class PortalLoadBoardRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/vtc/portal/loads", (
            string guildId,
            string? status,
            [FromServices] PortalDataStore store,
            [FromServices] DispatchLoadStore dispatchLoadStore) =>
        {
            if (string.IsNullOrWhiteSpace(guildId)) return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            var guild = store.GetGuild(guildId);
            var portalRows = guild.DispatchLoads.ToList();
            var legacyRows = dispatchLoadStore.List(guildId).Select(load => new PortalDispatchLoad
            {
                Id = load.Id,
                LoadNumber = load.LoadNumber,
                Status = NormalizePortalLoadStatus(load.Status),
                Title = load.LoadNumber,
                Cargo = load.Commodity,
                Origin = load.PickupLocation,
                Destination = load.DropoffLocation,
                AssignedDriver = load.DriverName,
                AssignedDriverDiscordUserId = load.DriverDiscordUserId,
                AssignedTruck = load.TruckId,
                Dispatcher = "ELD Dispatch Tracker",
                Notes = load.DispatcherNotes,
                BolUrl = load.BolNumber,
                IsCompanyLoad = true,
                CreatedUtc = load.CreatedUtc,
                UpdatedUtc = load.UpdatedUtc,
                ClaimedUtc = load.AssignedUtc,
                PickedUpUtc = load.PickupUtc,
                DeliveredUtc = load.DeliveredUtc
            }).ToList();
            var merged = legacyRows.Concat(portalRows)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.LoadNumber) ? x.Id : x.LoadNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
                .ToList();
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
                merged = merged.Where(x => string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Ok(new { ok = true, guildId, loads = merged.OrderByDescending(x => x.UpdatedUtc).ToList(), portalCount = portalRows.Count, dispatchCount = legacyRows.Count });
        });

        app.MapPost("/api/vtc/portal/loads", async (HttpContext ctx, [FromServices] PortalDataStore store) =>
        {
            var guildId = ctx.Request.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId)) return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            var row = await ctx.Request.ReadFromJsonAsync<PortalDispatchLoad>() ?? new PortalDispatchLoad();
            var saved = store.UpdateGuild(guildId, g =>
            {
                if (string.IsNullOrWhiteSpace(row.Id)) row.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(row.LoadNumber)) row.LoadNumber = "OW-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (string.IsNullOrWhiteSpace(row.Status)) row.Status = "Available";
                if (row.CreatedUtc == default) row.CreatedUtc = DateTimeOffset.UtcNow;
                row.UpdatedUtc = DateTimeOffset.UtcNow;
                var idx = g.DispatchLoads.FindIndex(x => x.Id == row.Id || x.LoadNumber == row.LoadNumber);
                if (idx >= 0) g.DispatchLoads[idx] = row; else g.DispatchLoads.Add(row);
                AddLog(g, "Load saved", row.LoadNumber);
            });
            return Results.Ok(new { ok = true, loads = saved.DispatchLoads, load = saved.DispatchLoads.FirstOrDefault(x => x.Id == row.Id) });
        });

        app.MapPost("/api/vtc/portal/loads/{id}/assign", async (string id, HttpContext ctx, [FromServices] PortalDataStore store) =>
        {
            var guildId = ctx.Request.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId)) return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            var req = await ctx.Request.ReadFromJsonAsync<AssignReq>() ?? new AssignReq();
            PortalDispatchLoad? row = null;
            store.UpdateGuild(guildId, g =>
            {
                row = g.DispatchLoads.FirstOrDefault(x => x.Id == id || x.LoadNumber == id);
                if (row == null) return;
                row.Status = "Assigned";
                row.AssignedDriver = First(req.DriverName, row.AssignedDriver);
                row.AssignedDriverDiscordUserId = First(req.DriverDiscordUserId, row.AssignedDriverDiscordUserId);
                row.AssignedTruck = First(req.Truck, row.AssignedTruck);
                row.ClaimedUtc = DateTimeOffset.UtcNow;
                row.UpdatedUtc = DateTimeOffset.UtcNow;
                AddLog(g, "Load assigned", row.LoadNumber);
            });
            return row == null ? Results.NotFound(new { ok = false, error = "LoadNotFound" }) : Results.Ok(new { ok = true, load = row });
        });

        app.MapPost("/api/vtc/portal/loads/{id}/status", async (string id, HttpContext ctx, [FromServices] PortalDataStore store) =>
        {
            var guildId = ctx.Request.Query["guildId"].ToString();
            if (string.IsNullOrWhiteSpace(guildId)) return Results.BadRequest(new { ok = false, error = "MissingGuildId" });
            var req = await ctx.Request.ReadFromJsonAsync<StatusReq>() ?? new StatusReq();
            PortalDispatchLoad? row = null;
            store.UpdateGuild(guildId, g =>
            {
                row = g.DispatchLoads.FirstOrDefault(x => x.Id == id || x.LoadNumber == id);
                if (row == null) return;
                row.Status = First(req.Status, row.Status);
                row.Notes = First(req.Notes, row.Notes);
                row.BolUrl = First(req.BolUrl, row.BolUrl);
                row.ReceiptUrl = First(req.ReceiptUrl, row.ReceiptUrl);
                row.DiscordMessageUrl = First(req.DiscordMessageUrl, row.DiscordMessageUrl);
                row.UpdatedUtc = DateTimeOffset.UtcNow;
                var s = row.Status.Replace(" ", "").ToLowerInvariant();
                if (s == "pickedup") row.PickedUpUtc = DateTimeOffset.UtcNow;
                if (s == "delivered") row.DeliveredUtc = DateTimeOffset.UtcNow;
                if (s == "paid") row.PaidUtc = DateTimeOffset.UtcNow;
                AddLog(g, "Load status", row.LoadNumber + " -> " + row.Status);
            });
            return row == null ? Results.NotFound(new { ok = false, error = "LoadNotFound" }) : Results.Ok(new { ok = true, load = row });
        });
    }

    private static string NormalizePortalLoadStatus(string? status)
    {
        var s = (status ?? "").Trim().Replace("_", " ").ToLowerInvariant();
        return s switch
        {
            "unassigned" => "Available",
            "assigned" => "Assigned",
            "picked up" => "Picked Up",
            "pickedup" => "Picked Up",
            "in transit" => "In Transit",
            "delivered" => "Delivered",
            "paid" => "Paid",
            "cancelled" => "Cancelled",
            "canceled" => "Cancelled",
            _ => string.IsNullOrWhiteSpace(status) ? "Available" : status.Trim()
        };
    }

    private static void AddLog(PortalGuildData g, string action, string detail)
    {
        g.AuditLog.Add(new PortalAuditEntry { Action = action, Detail = detail, Actor = "Portal Load Board", CreatedUtc = DateTimeOffset.UtcNow });
    }

    private static string First(params string?[] values)
    {
        foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }

    private sealed class AssignReq { public string DriverName { get; set; } = ""; public string DriverDiscordUserId { get; set; } = ""; public string Truck { get; set; } = ""; }
    private sealed class StatusReq { public string Status { get; set; } = ""; public string Notes { get; set; } = ""; public string BolUrl { get; set; } = ""; public string ReceiptUrl { get; set; } = ""; public string DiscordMessageUrl { get; set; } = ""; }
}