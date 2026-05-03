namespace OverWatchELD.VtcBot.Routes;

public static class MapAssetRoutes
{
    private const string RemoteBaseUrl =
        "https://github.com/GasMonkey87/OverWatchELD-Bot/releases/download/Maps/";

    private static readonly HashSet<string> AllowedRemoteFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ats.pmtiles",
        "sprites.json",
        "sprites.png",
        "sprites@2x.json",
        "sprites@2x.png"
    };

    private static readonly HashSet<string> AllowedTileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png",
        "jpg",
        "jpeg",
        "webp"
    };

    // 1x1 transparent PNG. Returned when a tile does not exist so MapLibre does not spam red 404s.
    private static readonly byte[] EmptyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    public static WebApplication MapMapAssetRoutes(this WebApplication app)
    {
        // Local uploaded raster tiles.
        // Put tiles in either:
        //   wwwroot/map-tiles/{z}/{x}/{y}.png
        //   data/map-tiles/{z}/{x}/{y}.png
        // Supported extensions: png, jpg, jpeg, webp.
        app.MapGet("/map-tiles/{z:int}/{x:int}/{y}.{ext}", async (
            int z,
            int x,
            int y,
            string ext,
            HttpContext ctx) =>
        {
            if (!AllowedTileExtensions.Contains(ext))
                return Results.NotFound();

            var tilePath = FindLocalTilePath(z, x, y, ext);
            if (tilePath == null)
            {
                ctx.Response.Headers.CacheControl = "public,max-age=300";
                return Results.File(EmptyPng, "image/png");
            }

            var contentType = GetTileContentType(ext);
            ctx.Response.Headers.CacheControl = "public,max-age=604800,immutable";
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            return Results.File(tilePath, contentType, enableRangeProcessing: true);
        });

        app.MapGet("/api/map/tile-status", () =>
        {
            var roots = GetTileRoots()
                .Select(path => new
                {
                    path,
                    exists = Directory.Exists(path),
                    tileCount = Directory.Exists(path)
                        ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                            .Count(f => AllowedTileExtensions.Contains(Path.GetExtension(f).TrimStart('.')))
                        : 0
                })
                .ToArray();

            return Results.Json(new
            {
                ok = true,
                tileUrl = "/map-tiles/{z}/{x}/{y}.png",
                roots
            });
        });

        // Existing remote PMTiles/sprite proxy kept for compatibility.
        app.MapGet("/map-assets/{file}", async (
            string file,
            HttpContext ctx,
            IHttpClientFactory httpClientFactory) =>
        {
            if (!AllowedRemoteFiles.Contains(file))
                return Results.NotFound();

            var client = httpClientFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, RemoteBaseUrl + Uri.EscapeDataString(file));

            if (ctx.Request.Headers.TryGetValue("Range", out var range))
                req.Headers.TryAddWithoutValidation("Range", range.ToString());

            var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            ctx.Response.StatusCode = (int)res.StatusCode;

            foreach (var h in res.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();

            foreach (var h in res.Content.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();

            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers.Remove("transfer-encoding");

            await res.Content.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });

        return app;
    }

    private static string[] GetTileRoots()
    {
        var baseDir = AppContext.BaseDirectory;
        var currentDir = Directory.GetCurrentDirectory();

        return new[]
        {
            Path.Combine(currentDir, "wwwroot", "map-tiles"),
            Path.Combine(baseDir, "wwwroot", "map-tiles"),
            Path.Combine(currentDir, "data", "map-tiles"),
            Path.Combine(baseDir, "data", "map-tiles"),
            Path.Combine(currentDir, "maps", "tiles"),
            Path.Combine(baseDir, "maps", "tiles")
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static string? FindLocalTilePath(int z, int x, int y, string requestedExt)
    {
        foreach (var root in GetTileRoots())
        {
            var exact = Path.Combine(root, z.ToString(), x.ToString(), $"{y}.{requestedExt}");
            if (File.Exists(exact))
                return exact;

            foreach (var ext in AllowedTileExtensions)
            {
                var fallback = Path.Combine(root, z.ToString(), x.ToString(), $"{y}.{ext}");
                if (File.Exists(fallback))
                    return fallback;
            }
        }

        return null;
    }

    private static string GetTileContentType(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
    }
}
