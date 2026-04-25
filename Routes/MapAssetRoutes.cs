namespace OverWatchELD.VtcBot.Routes;

public static class MapAssetRoutes
{
    private const string BaseUrl =
        "https://github.com/GasMonkey87/OverWatchELD-Bot/releases/download/Maps/";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "ats.pmtiles",
        "sprites.json",
        "sprites.png",
        "sprites@2x.json",
        "sprites@2x.png"
    };

    public static WebApplication MapMapAssetRoutes(this WebApplication app)
    {
        app.MapGet("/map-assets/{file}", async (
            string file,
            HttpContext ctx,
            IHttpClientFactory httpClientFactory) =>
        {
            if (!Allowed.Contains(file))
                return Results.NotFound();

            var client = httpClientFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + Uri.EscapeDataString(file));

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
}
