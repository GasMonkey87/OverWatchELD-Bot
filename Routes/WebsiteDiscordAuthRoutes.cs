using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class WebsiteDiscordAuthRoutes
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/auth/discord/login", (HttpContext ctx, DiscordOAuthService oauth) =>
        {
            var state = Guid.NewGuid().ToString("N");
            var returnUrl = ctx.Request.Query["returnUrl"].ToString();
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
            ctx.Response.Cookies.Append("ow_use_portal_callback", "1", cookie);

            return Results.Redirect(oauth.BuildAuthorizeUrl(state));
        });

        app.MapGet("/api/auth/discord/callback", async (
            HttpContext ctx,
            DiscordOAuthService oauth,
            WebSessionStore sessionStore,
            VtcAccessService vtcAccess,
            CancellationToken ct) =>
        {
            var error = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrWhiteSpace(error))
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "discord_denied"));

            var code = ctx.Request.Query["code"].ToString();
            var state = ctx.Request.Query["state"].ToString();
            var expectedState = ctx.Request.Cookies["ow_oauth_state"];

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) ||
                string.IsNullOrWhiteSpace(expectedState) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "invalid_state"));
            }

            var tokenRes = await oauth.ExchangeCodeAsync(code, ct);
            if (tokenRes == null || string.IsNullOrWhiteSpace(tokenRes.AccessToken))
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "token_failed"));

            var user = await oauth.GetCurrentUserAsync(tokenRes.AccessToken, ct);
            if (user == null || string.IsNullOrWhiteSpace(user.Id))
                return Results.Redirect(BuildPortalRedirect(ctx, "", "", "user_failed"));

            var guilds = await oauth.GetCurrentUserGuildsAsync(tokenRes.AccessToken, ct);
            var matches = vtcAccess.MatchSupportedVtcs(user.Id, guilds);
            var selectedGuildId = matches.FirstOrDefault()?.GuildId ?? "";
            var sessionId = Guid.NewGuid().ToString("N");

            sessionStore.Save(sessionId, new WebSessionUser
            {
                AccountId = "",
                Email = "",
                IsEmailAccount = false,
                DiscordUserId = user.Id,
                Username = user.Username,
                GlobalName = user.GlobalName,
                AccessToken = tokenRes.AccessToken,
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

            if (!string.IsNullOrWhiteSpace(selectedGuildId))
            {
                ctx.Response.Cookies.Append("ow_selected_guild", selectedGuildId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                    IsEssential = true
                });
            }

            ctx.Response.Cookies.Delete("ow_oauth_state");
            ctx.Response.Cookies.Delete("ow_use_portal_callback");
            ctx.Response.Cookies.Delete("ow_portal_return_url");

            return Results.Redirect(BuildPortalRedirect(ctx, sessionId, selectedGuildId, ""));
        });
    }

    private static string BuildPortalRedirect(HttpContext ctx, string token, string guildId, string error)
    {
        var portalBase = (Environment.GetEnvironmentVariable("OVERWATCH_PORTAL_BASE_URL")
            ?? Environment.GetEnvironmentVariable("PUBLIC_PORTAL_BASE_URL")
            ?? "https://overwatcheld.com")
            .Trim()
            .TrimEnd('/');

        var returnUrl = ctx.Request.Cookies["ow_portal_return_url"];
        if (string.IsNullOrWhiteSpace(returnUrl))
            returnUrl = "/portal/";

        if (!returnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!returnUrl.StartsWith("/"))
                returnUrl = "/" + returnUrl.TrimStart('.', '/');
            returnUrl = portalBase + returnUrl;
        }

        var separator = returnUrl.Contains('?') ? "&" : "?";
        if (!string.IsNullOrWhiteSpace(error))
            return returnUrl + separator + "error=" + Uri.EscapeDataString(error);

        return returnUrl + separator + "token=" + Uri.EscapeDataString(token) + "&guildId=" + Uri.EscapeDataString(guildId);
    }
}
