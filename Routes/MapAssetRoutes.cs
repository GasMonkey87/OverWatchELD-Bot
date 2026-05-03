using Microsoft.Net.Http.Headers;

namespace OverWatchELD.VtcBot.Routes;

public static class MapAssetRoutes
{
    private const string ReleaseBaseUrl =
        "https://github.com/GasMonkey87/OverWatchELD-Bot/releases/download/Maps/";

    private static readonly HashSet<string> AllowedAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "ats.pmtiles",
        "sprites.json",
        "sprites.png",
        "sprites@2x.json",
        "sprites@2x.png"
    };

    public static WebApplication MapMapAssetRoutes(this WebApplication app)
    {
        // Normal XYZ tile URL used by live-map.html:
        // /map-tiles/{z}/{x}/{y}.png
        app.MapGet("/map-tiles/{z:int}/{x:int}/{y:int}.png", async (
            int z,
            int x,
            int y,
            HttpContext ctx) =>
        {
            var local = FindTilePath(z, x, y);
            if (local == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Tile not uploaded");
                return;
            }

            ctx.Response.Headers.CacheControl = "public,max-age=86400";
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            await ctx.Response.SendFileAsync(local);
        });

        app.MapGet("/api/map/tiles/status", () =>
        {
            var root = FirstExistingTileRoot();
            var sample = root == null ? null : FindFirstTile(root);

            return Results.Ok(new
            {
                ok = true,
                hasTiles = root != null && sample != null,
                root = root ?? "",
                sample = sample ?? "",
                expected = new[]
                {
                    "wwwroot/map-tiles/{z}/{x}/{y}.png",
                    "data/map-tiles/{z}/{x}/{y}.png"
                }
            });
        });

        // Existing release-hosted map assets remain supported.
        app.MapGet("/map-assets/{file}", async (
            string file,
            HttpContext ctx,
            IHttpClientFactory httpClientFactory) =>
        {
            if (!AllowedAssets.Contains(file))
                return Results.NotFound();

            var client = httpClientFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, ReleaseBaseUrl + Uri.EscapeDataString(file));

            if (ctx.Request.Headers.TryGetValue(HeaderNames.Range, out var range))
                req.Headers.TryAddWithoutValidation(HeaderNames.Range, range.ToString());

            var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            ctx.Response.StatusCode = (int)res.StatusCode;

            foreach (var h in res.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();

            foreach (var h in res.Content.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();

            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            ctx.Response.Headers.Remove("transfer-encoding");

            await res.Content.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });

        return app;
    }

    private static string? FindTilePath(int z, int x, int y)
    {
        foreach (var root in CandidateTileRoots())
        {
            var path = Path.Combine(root, z.ToString(), x.ToString(), y + ".png");
            if (File.Exists(path))
                return path;

            var jpg = Path.Combine(root, z.ToString(), x.ToString(), y + ".jpg");
            if (File.Exists(jpg))
                return jpg;

            var webp = Path.Combine(root, z.ToString(), x.ToString(), y + ".webp");
            if (File.Exists(webp))
                return webp;
        }

        return null;
    }

    private static string? FirstExistingTileRoot()
        => CandidateTileRoots().FirstOrDefault(Directory.Exists);

    private static string? FindFirstTile(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(p =>
                    p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidateTileRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "wwwroot", "map-tiles");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "map-tiles");
        yield return Path.Combine(AppContext.BaseDirectory, "data", "map-tiles");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "data", "map-tiles");
        yield return "/data/map-tiles";
    }
}
