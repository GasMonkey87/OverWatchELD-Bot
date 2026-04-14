using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OverWatchELD.VtcBot.Performance;

public static class PerformanceApi
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public static bool TryHandle(HttpListenerContext ctx, PerformanceStore store)
    {
        var req = ctx.Request;
        var path = (req.Url?.AbsolutePath ?? "").TrimEnd('/').ToLowerInvariant();

        // POST /api/performance/update?guildId=...
        if (path == "/api/performance/update" && req.HttpMethod == "POST")
        {
            var guildId = req.QueryString["guildId"] ?? "";
            if (string.IsNullOrWhiteSpace(guildId)) return WriteJson(ctx, 400, new { ok = false, error = "missing guildId" });

            using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = sr.ReadToEnd();

            DriverPerformance? payload;
            try { payload = JsonSerializer.Deserialize<DriverPerformance>(body, ReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.DiscordUserId))
                return WriteJson(ctx, 400, new { ok = false, error = "invalid payload" });

            store.Upsert(guildId, payload);

            var (rank, total) = store.GetRank(guildId, payload.DiscordUserId);
            return WriteJson(ctx, 200, new { ok = true, rank, total, score = payload.Score });
        }

        // GET /api/performance/top?guildId=...&take=10
        if (path == "/api/performance/top" && req.HttpMethod == "GET")
        {
            var guildId = req.QueryString["guildId"] ?? "";
            if (string.IsNullOrWhiteSpace(guildId)) return WriteJson(ctx, 400, new { ok = false, error = "missing guildId" });

            var takeStr = req.QueryString["take"] ?? "10";
            _ = int.TryParse(takeStr, out var take);
            if (take <= 0) take = 10;
            if (take > 50) take = 50;

            var top = store.GetTop(guildId, take);
            return WriteJson(ctx, 200, new { ok = true, top });
        }

        return false;
    }

    private static bool WriteJson(HttpListenerContext ctx, int status, object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentEncoding = Encoding.UTF8;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
        return true;
    }
}
