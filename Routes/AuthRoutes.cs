using System;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace OverWatchELD.VtcBot.Routes;

public static class AuthRoutes
{
    public static void Register(WebApplication app)
    {
        var clientId = Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID") ?? "";
        var redirect = Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI") ?? "";

        var oauth = new DiscordOAuthService();

        app.MapGet("/auth/login", () =>
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirect))
                return Results.Redirect("/login.html?error=missing_oauth_config");

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
                return Results.Redirect("/login.html?error=missing_code");

            var clientSecret = Environment.GetEnvironmentVariable("DISCORD_CLIENT_SECRET") ?? "";
            var redirectUri = Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI") ?? "";

            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(redirectUri))
            {
                return Results.Redirect("/login.html?error=missing_oauth_config");
            }

            try
            {
                var token = await oauth.ExchangeCodeAsync(
                    code,
                    clientId,
                    clientSecret,
                    redirectUri
                );

                var user = await oauth.GetUserAsync(token);
                var guilds = await oauth.GetGuildsAsync(token);

                ctx.Session.SetString("discord_user", user.GetRawText());
                ctx.Session.SetString("discord_guilds", guilds.GetRawText());

                return Results.Redirect("/index.html");
            }
            catch
            {
                return Results.Redirect("/login.html?error=oauth_failed");
            }
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var userJson = ctx.Session.GetString("discord_user");
            var guildsJson = ctx.Session.GetString("discord_guilds");

            if (string.IsNullOrWhiteSpace(userJson) || string.IsNullOrWhiteSpace(guildsJson))
                return Results.Json(new { ok = false });

            using var userDoc = JsonDocument.Parse(userJson);
            using var guildsDoc = JsonDocument.Parse(guildsJson);

            return Results.Json(new
            {
                ok = true,
                user = userDoc.RootElement.Clone(),
                guilds = guildsDoc.RootElement.Clone()
            });
        });

        app.MapPost("/auth/logout", (HttpContext ctx) =>
        {
            ctx.Session.Clear();
            return Results.Ok(new { ok = true });
        });
    }
}
