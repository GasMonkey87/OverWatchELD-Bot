using Microsoft.AspNetCore.Http;

public static class AuthRoutes
{
    public static void Register(WebApplication app)
    {
        var clientId = Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID");
        var redirect = Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI");

        var oauth = new DiscordOAuthService();

        app.MapGet("/auth/login", () =>
        {
            var url =
                $"https://discord.com/api/oauth2/authorize?client_id={clientId}" +
                $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}" +
                $"&scope=identify%20guilds";

            return Results.Redirect(url);
        });

        app.MapGet("/auth/callback", async (HttpContext ctx) =>
        {
            var code = ctx.Request.Query["code"].ToString();
            if (string.IsNullOrWhiteSpace(code))
                return Results.Redirect("/login.html");

            var token = await oauth.ExchangeCodeAsync(
                code,
                Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID")!,
                Environment.GetEnvironmentVariable("DISCORD_CLIENT_SECRET")!,
                Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI")!
            );

            var user = await oauth.GetUserAsync(token);
            var guilds = await oauth.GetGuildsAsync(token);

            ctx.Session.SetString("discord_user", user.ToString());
            ctx.Session.SetString("discord_guilds", guilds.ToString());

            return Results.Redirect("/index.html");
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var user = ctx.Session.GetString("discord_user");
            var guilds = ctx.Session.GetString("discord_guilds");

            if (user == null)
                return Results.Json(new { ok = false });

            return Results.Json(new
            {
                ok = true,
                user = JsonDocument.Parse(user).RootElement,
                guilds = JsonDocument.Parse(guilds!).RootElement
            });
        });

        app.MapPost("/auth/logout", (HttpContext ctx) =>
        {
            ctx.Session.Clear();
            return Results.Ok(new { ok = true });
        });
    }
}
