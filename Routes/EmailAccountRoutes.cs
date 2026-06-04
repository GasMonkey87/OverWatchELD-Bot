using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class EmailAccountRoutes
{
    public static void Register(
        WebApplication app,
        EmailAccountStore accountStore,
        WebSessionStore sessionStore)
    {
        app.MapPost("/api/account/register", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<RegisterRequest>();

            if (req == null)
                return Results.Json(new { ok = false, error = "BadRequest" }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.Json(new { ok = false, error = "Email is required." }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
                return Results.Json(new { ok = false, error = "Password must be at least 8 characters." }, statusCode: 400);

            try
            {
                var created = accountStore.CreateAccount(req.Email, req.Password, req.DisplayName);

                return Results.Json(new
                {
                    ok = created,
                    error = created ? null : "Email already exists."
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = ex.Message
                }, statusCode: 400);
            }
        });

        app.MapPost("/api/account/login", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<LoginRequest>();

            if (req == null)
                return Results.Json(new { ok = false, error = "BadRequest" }, statusCode: 400);

            var account = accountStore.ValidateLogin(req.Email, req.Password);

            if (account == null)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "Invalid email or password."
                }, statusCode: 401);
            }

            var sessionId = Guid.NewGuid().ToString("N");
            SaveSession(sessionStore, sessionId, account);

            ctx.Response.Cookies.Append(
                "ow_session",
                sessionId,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                    IsEssential = true
                });

            return Results.Json(new
            {
                ok = true,
                redirectUrl = "/driver-home.html"
            });
        });

        app.MapGet("/api/account/me", (HttpContext ctx) =>
        {
            var session = GetSession(ctx, sessionStore);
            if (session == null)
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            EmailAccount? account = null;
            if (!string.IsNullOrWhiteSpace(session.AccountId))
                account = accountStore.FindById(session.AccountId);

            return Results.Json(new
            {
                ok = true,
                data = ToAccountDto(account, session)
            });
        });

        app.MapPost("/api/account/update-profile", async (HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Cookies["ow_session"];
            var session = GetSession(ctx, sessionStore);

            if (session == null || string.IsNullOrWhiteSpace(session.AccountId) || string.IsNullOrWhiteSpace(sessionId))
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var req = await ctx.Request.ReadFromJsonAsync<UpdateProfileRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.DisplayName))
                return Results.Json(new { ok = false, error = "Display name is required." }, statusCode: 400);

            try
            {
                var account = accountStore.UpdateProfile(
                    session.AccountId,
                    req.DisplayName,
                    req.Phone,
                    req.HomeCity,
                    req.HomeState,
                    req.PreferredTruck,
                    req.Company,
                    req.Bio,
                    req.AvatarUrl);

                if (account == null)
                    return Results.Json(new { ok = false, error = "AccountNotFound" }, statusCode: 404);

                SaveSession(sessionStore, sessionId, account);

                return Results.Json(new
                {
                    ok = true,
                    data = ToAccountDto(account, null)
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/account/change-password", async (HttpContext ctx) =>
        {
            var session = GetSession(ctx, sessionStore);

            if (session == null || string.IsNullOrWhiteSpace(session.AccountId))
                return Results.Json(new { ok = false, error = "NotAuthenticated" }, statusCode: 401);

            var req = await ctx.Request.ReadFromJsonAsync<ChangePasswordRequest>();
            if (req == null)
                return Results.Json(new { ok = false, error = "BadRequest" }, statusCode: 400);

            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
                return Results.Json(new { ok = false, error = "New password must be at least 8 characters." }, statusCode: 400);

            if (!string.Equals(req.NewPassword, req.ConfirmPassword, StringComparison.Ordinal))
                return Results.Json(new { ok = false, error = "Passwords do not match." }, statusCode: 400);

            try
            {
                var changed = accountStore.ChangePassword(session.AccountId, req.CurrentPassword, req.NewPassword);
                if (!changed)
                    return Results.Json(new { ok = false, error = "Current password is incorrect." }, statusCode: 400);

                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
            }
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

            ctx.Session.Remove("discord_user");
            ctx.Session.Remove("discord_guilds");

            return Results.Json(new { ok = true });
        });
    }

    private static WebSessionUser? GetSession(HttpContext ctx, WebSessionStore sessionStore)
    {
        var sessionId = ctx.Request.Cookies["ow_session"];
        if (string.IsNullOrWhiteSpace(sessionId) || !sessionStore.TryGet(sessionId, out var session) || session == null)
            return null;
        return session;
    }

    private static void SaveSession(WebSessionStore sessionStore, string sessionId, EmailAccount account)
    {
        sessionStore.Save(
            sessionId,
            new WebSessionUser
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

    private static object ToAccountDto(EmailAccount? account, WebSessionUser? session)
    {
        return new
        {
            accountId = account?.Id ?? session?.AccountId ?? "",
            email = account?.Email ?? session?.Email ?? "",
            displayName = account?.DisplayName ?? session?.Username ?? "Driver",
            phone = account?.Phone ?? "",
            homeCity = account?.HomeCity ?? "",
            homeState = account?.HomeState ?? "",
            preferredTruck = account?.PreferredTruck ?? "",
            company = account?.Company ?? "",
            bio = account?.Bio ?? "",
            avatarUrl = account?.AvatarUrl ?? "",
            isEmailAccount = session?.IsEmailAccount ?? true,
            discordLinked = !string.IsNullOrWhiteSpace(account?.DiscordUserId ?? session?.DiscordUserId),
            discordUserId = account?.DiscordUserId ?? session?.DiscordUserId ?? "",
            discordUsername = account?.DiscordUsername ?? "",
            discordGlobalName = account?.DiscordGlobalName ?? session?.GlobalName ?? "",
            createdUtc = account?.CreatedUtc,
            updatedUtc = account?.UpdatedUtc,
            lastLoginUtc = account?.LastLoginUtc
        };
    }

    private sealed class RegisterRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    private sealed class UpdateProfileRequest
    {
        public string DisplayName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string HomeCity { get; set; } = "";
        public string HomeState { get; set; } = "";
        public string PreferredTruck { get; set; } = "";
        public string Company { get; set; } = "";
        public string Bio { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
    }

    private sealed class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = "";
        public string NewPassword { get; set; } = "";
        public string ConfirmPassword { get; set; } = "";
    }

    private sealed class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
