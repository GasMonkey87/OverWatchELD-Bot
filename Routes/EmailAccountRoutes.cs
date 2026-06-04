using Microsoft.AspNetCore.Http;
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
var req =
await ctx.Request.ReadFromJsonAsync<RegisterRequest>();

```
        if (req == null)
            return Results.BadRequest();

        var created =
            accountStore.CreateAccount(
                req.Email,
                req.Password,
                req.DisplayName);

        return Results.Json(new
        {
            ok = created,
            error = created ? null : "Email already exists"
        });
    });

    app.MapPost("/api/account/login", async (HttpContext ctx) =>
    {
        var req =
            await ctx.Request.ReadFromJsonAsync<LoginRequest>();

        if (req == null)
            return Results.BadRequest();

        var account =
            accountStore.ValidateLogin(
                req.Email,
                req.Password);

        if (account == null)
        {
            return Results.Json(new
            {
                ok = false,
                error = "Invalid email or password"
            });
        }

        var sessionId = Guid.NewGuid().ToString("N");

        sessionStore.Save(
            sessionId,
            new WebSessionUser
            {
                DiscordUserId = account.Id,
                Username = account.DisplayName,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            });

        ctx.Response.Cookies.Append(
            "ow_session",
            sessionId,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30)
            });

        return Results.Json(new
        {
            ok = true,
            redirectUrl = "/driver-home.html"
        });
    });

    app.MapPost("/api/account/logout", (HttpContext ctx) =>
    {
        ctx.Response.Cookies.Delete("ow_session");

        return Results.Json(new
        {
            ok = true
        });
    });
}

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
```

}
