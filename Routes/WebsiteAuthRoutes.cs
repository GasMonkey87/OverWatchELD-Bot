using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class WebsiteAuthRoutes
{
    public static void Register(WebApplication app, EmailAccountStore accountStore, WebSessionStore sessionStore)
    {
        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<WebsiteLoginRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new { ok = false, error = "Email and password are required." }, statusCode: 400);

            var account = accountStore.ValidateLogin(req.Email, req.Password);
            if (account == null)
                return Results.Json(new { ok = false, error = "Invalid email or password." }, statusCode: 401);

            var sessionId = Guid.NewGuid().ToString("N");
            sessionStore.Save(sessionId, new WebSessionUser
            {
                AccountId = account.Id,
                Email = account.Email,
                IsEmailAccount = true,
                DiscordUserId = account.DiscordUserId,
                Username = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName,
                GlobalName = string.IsNullOrWhiteSpace(account.DiscordGlobalName) ? account.DisplayName : account.DiscordGlobalName,
                AccessToken = "",
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            });

            ctx.Response.Cookies.Append("ow_session", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true
            });

            return Results.Json(new
            {
                ok = true,
                token = sessionId,
                sessionToken = sessionId,
                accessToken = sessionId,
                accountId = account.Id,
                email = account.Email,
                displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName,
                discordLinked = !string.IsNullOrWhiteSpace(account.DiscordUserId),
                discordUserId = account.DiscordUserId ?? "",
                guildId = ResolveFirstGuildId(ctx, account.DiscordUserId)
            });
        });

        app.MapGet("/api/auth/discord/login", (HttpContext ctx) =>
        {
            var oauth = ctx.RequestServices.GetRequiredService<DiscordOAuthService>();
            var state = Guid.NewGuid().ToString("N");
            var returnUrl = ctx.Request.Query["returnUrl"].ToString();
            var callbackUrl = ctx.Request.Query["callbackUrl"].ToString();

            if (string.IsNullOrWhiteSpace(returnUrl))
                returnUrl = "/portal/";

            var cookie = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10),
                IsEssential = true
            };

            ctx.Response.Cookies.Append("ow_oauth_state", state, cookie);
            ctx.Response.Cookies.Append("ow_portal_return_url", returnUrl, cookie);
            if (!string.IsNullOrWhiteSpace(callbackUrl))
                ctx.Response.Cookies.Append("ow_portal_callback_url", callbackUrl, cookie);

            return Results.Redirect(oauth.BuildAuthorizeUrl(state));
        });
    }

    private static string ResolveFirstGuildId(HttpContext ctx, string? discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId))
            return "";

        try
        {
            var client = ctx.RequestServices.GetService<DiscordSocketClient>();
            if (client == null)
                return "";

            foreach (var guild in client.Guilds)
            {
                var user = guild.GetUser(ulong.Parse(discordUserId));
                if (user != null)
                    return guild.Id.ToString();
            }
        }
        catch { }

        return "";
    }

    private sealed class WebsiteLoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
