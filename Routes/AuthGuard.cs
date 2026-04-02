// Routes/AuthGuard.cs
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace OverWatchELD.VtcBot.Routes;

public static class AuthGuard
{
    public static bool TryGetSession(HttpContext ctx, out JsonElement user, out JsonElement guilds)
    {
        user = default;
        guilds = default;

        var userJson = ctx.Session.GetString("discord_user");
        var guildsJson = ctx.Session.GetString("discord_guilds");

        if (string.IsNullOrWhiteSpace(userJson) || string.IsNullOrWhiteSpace(guildsJson))
            return false;

        user = JsonDocument.Parse(userJson).RootElement.Clone();
        guilds = JsonDocument.Parse(guildsJson).RootElement.Clone();
        return true;
    }

    public static bool IsLoggedIn(HttpContext ctx)
    {
        return !string.IsNullOrWhiteSpace(ctx.Session.GetString("discord_user"));
    }

    public static bool CanManageGuild(HttpContext ctx, string guildId)
    {
        if (!TryGetSession(ctx, out _, out var guilds))
            return false;

        foreach (var g in guilds.EnumerateArray())
        {
            var id = g.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            if (!string.Equals(id, guildId, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var permsText = g.TryGetProperty("permissions", out var p) ? p.GetString() ?? "0" : "0";
            if (!ulong.TryParse(permsText, out var perms))
                perms = 0;

            var isAdmin = (perms & 0x8UL) != 0;
            var canManageGuild = (perms & 0x20UL) != 0;
            return isAdmin || canManageGuild;
        }

        return false;
    }
}
