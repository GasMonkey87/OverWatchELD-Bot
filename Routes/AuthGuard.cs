using System;
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
            var id = ReadJsonString(g, "id");
            if (!string.Equals(id, guildId, StringComparison.OrdinalIgnoreCase))
                continue;

            var isOwner = ReadJsonBool(g, "owner");

            var perms =
                ReadJsonULong(g, "permissions_new") ??
                ReadJsonULong(g, "permissions") ??
                0UL;

            var isAdmin = (perms & 0x8UL) != 0;
            var canManageGuild = (perms & 0x20UL) != 0;

            return isOwner || isAdmin || canManageGuild;
        }

        return false;
    }

    private static string ReadJsonString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var p))
            return "";

        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? "",
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => p.ToString() ?? ""
        };
    }

    private static bool ReadJsonBool(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var p))
            return false;

        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(p.GetString(), out var b) && b,
            JsonValueKind.Number => p.GetRawText() == "1",
            _ => false
        };
    }

    private static ulong? ReadJsonULong(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var p))
            return null;

        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetUInt64(out var n))
                return n;

            if (ulong.TryParse(p.GetRawText(), out var n2))
                return n2;

            return null;
        }

        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (ulong.TryParse(s, out var n))
                return n;
        }

        return null;
    }
}
