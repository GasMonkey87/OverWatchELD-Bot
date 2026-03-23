using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace OverWatchELD.VtcBot.Routes;

public static class DashboardRoutes
{
    public static void Register(IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard", () => Results.Redirect("/index.html"));
        app.MapGet("/live-map", () => Results.Redirect("/live-map.html"));
    }
}
