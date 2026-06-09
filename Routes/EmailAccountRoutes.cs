using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class EmailAccountRoutes
{
    public static void Register(WebApplication app, EmailAccountStore accountStore, WebSessionStore sessionStore)
    {
        WebsiteDiscordAuthRoutes.Register(app);
        VtcDirectoryRoutes.Register(app);
        VtcSelectionRoutes.Register(app);
        PortalMeEmailRoutes.Register(app);
        
        app.MapPost("/api/account/register", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<RegisterRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new { ok = false, error = "Email and password are required." }, statusCode: 400);

            if (req.Password.Length < 8)
                return Results.Json(new { ok = false, error = "Password must be at least 8 characters." }, statusCode: 400);

            try
            {
                var created = accountStore.CreateAccount(req.Email, req.Password, req.DisplayName);
                return Results.Json(new { ok = created, error = created ? null : "Email already exists." });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/account/login", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            return Login(ctx, req, accountStore, sessionStore, false);
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            return Login(ctx, req, accountStore, sessionStore, true);
        });

        app.MapGet("/api/account/me", (HttpContext ctx) =>
        {
            var session = GetSession(ctx, sessionStore);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            EmailAccount? account = null;
            if (!string.IsNullOrWhiteSpace(session.AccountId))
                account = accountStore.FindById(session.AccountId);

            return Results.Json(new { ok = true, data = ToAccountDto(account, session) });
        });

        app.MapPost("/api/account/logout", (HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Cookies["ow_session"];
            if (!string.IsNullOrWhiteSpace(sessionId))
                sessionStore.Remove(sessionId);

            ctx.Response.Cookies.Delete("ow_session");
            ctx.Response.Cookies.Delete("ow_selected_guild");
            ctx.Response.Cookies.Delete("ow_oauth_state");
            ctx.Response.Cookies.Delete("ow_link_discord");
            ctx.Response.Cookies.Delete("ow_portal_return_url");
            ctx.Response.Cookies.Delete("ow_use_portal_callback");

            return Results.Json(new { ok = true });
        });
    }

    private static IResult Login(HttpContext ctx, LoginRequest? req, EmailAccountStore accountStore, WebSessionStore sessionStore, bool tokenResponse)
    {
        if (req == null)
            return Results.Json(new { ok = false, error = "BadRequest" }, statusCode: 400);

        var account = accountStore.ValidateLogin(req.Email, req.Password);
        if (account == null)
            return Results.Json(new { ok = false, error = "Invalid email or password." }, statusCode: 401);

        var sessionId = Guid.NewGuid().ToString("N");
        SaveSession(sessionStore, sessionId, account);

        ctx.Response.Cookies.Append("ow_session", sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            IsEssential = true
        });

        if (!tokenResponse)
            return Results.Json(new { ok = true, redirectUrl = "/driver-home.html" });

        return Results.Json(new
        {
            ok = true,
            token = sessionId,
            sessionToken = sessionId,
            accountId = account.Id,
            email = account.Email,
            displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName,
            discordLinked = !string.IsNullOrWhiteSpace(account.DiscordUserId),
            discordUserId = account.DiscordUserId ?? "",
            guildId = "",
            redirectUrl = "/select-vtc/"
        });
    }

    private static WebSessionUser? GetSession(HttpContext ctx, WebSessionStore sessionStore)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                sessionId = auth[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = ctx.Request.Headers["X-OverWatch-Session"].ToString();

        return !string.IsNullOrWhiteSpace(sessionId) && sessionStore.TryGet(sessionId, out var session) ? session : null;
    }

    private static void SaveSession(WebSessionStore sessionStore, string sessionId, EmailAccount account)
    {
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
    }

    private static object ToAccountDto(EmailAccount? account, WebSessionUser? session) => new
    {
        accountId = account?.Id ?? session?.AccountId ?? "",
        email = account?.Email ?? session?.Email ?? "",
        displayName = account?.DisplayName ?? session?.Username ?? "Driver",
        isEmailAccount = session?.IsEmailAccount ?? true,
        discordLinked = !string.IsNullOrWhiteSpace(account?.DiscordUserId ?? session?.DiscordUserId),
        discordUserId = account?.DiscordUserId ?? session?.DiscordUserId ?? "",
        discordUsername = account?.DiscordUsername ?? "",
        discordGlobalName = account?.DiscordGlobalName ?? session?.GlobalName ?? ""
    };

    private sealed class RegisterRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    private sealed class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
