using System.Reflection;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;

namespace OverWatchELD.VtcBot.Services;

public static class DiscordThreadService
{
    public static SocketGuild? ResolveGuild(DiscordSocketClient? client, string? gidStr)
    {
        if (client == null) return null;

        if (ulong.TryParse((gidStr ?? "").Trim(), out var gid) && gid != 0)
        {
            var g = client.Guilds.FirstOrDefault(x => x.Id == gid);
            if (g != null) return g;
        }

        return client.Guilds.FirstOrDefault();
    }

    public static ulong ThreadStoreTryGet(ThreadMapStore? threadStore, ulong guildId, ulong userId)
    {
        try
        {
            if (threadStore == null) return 0;
            var key = $"{guildId}:{userId}";
            return threadStore.TryGetThread(key, out var threadId) ? threadId : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void ThreadStoreSet(ThreadMapStore? threadStore, ulong guildId, ulong userId, ulong threadId)
    {
        try
        {
            if (threadStore == null) return;
            var key = $"{guildId}:{userId}";
            threadStore.SetThread(key, threadId);
        }
        catch { }
    }

    public static async Task<ulong> EnsureDriverThreadAsync(
        DispatchSettingsStore? dispatchStore,
        ThreadMapStore? threadStore,
        SocketGuild guild,
        ulong discordUserId,
        string label)
    {
        try
        {
            if (dispatchStore == null) return 0;

            var settings = dispatchStore.Get(guild.Id.ToString());
            if (!ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return 0;

            var dispatchChannel = guild.GetTextChannel(dispatchChId);
            if (dispatchChannel == null) return 0;

            var existing = ThreadStoreTryGet(threadStore, guild.Id, discordUserId);
            if (existing != 0) return existing;

            var starter = await dispatchChannel.SendMessageAsync($"📌 Dispatch thread created for **{label}**.");

            var thread = await dispatchChannel.CreateThreadAsync(
                name: $"dispatch-{SanitizeThreadName(label)}",
                autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                type: ThreadType.PrivateThread,
                invitable: false,
                message: starter
            );

            try
            {
                var u = guild.GetUser(discordUserId);
                if (u != null) await thread.AddUserAsync(u);
            }
            catch { }

            ThreadStoreSet(threadStore, guild.Id, discordUserId, thread.Id);
            return thread.Id;
        }
        catch
        {
            return 0;
        }
    }

    public static string SanitizeThreadName(string s)
    {
        s = (s ?? "driver").Trim().ToLowerInvariant();
        if (s.Length > 32) s = s[..32];
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "driver" : safe;
    }

    public static async Task<IMessageChannel?> ResolveChannelAsync(DiscordSocketClient? client, ulong channelId)
    {
        if (client == null) return null;

        if (client.GetChannel(channelId) is IMessageChannel cached)
            return cached;

        var rest = await client.Rest.GetChannelAsync(channelId);
        if (rest is RestThreadChannel rt) return rt;
        if (rest is RestTextChannel rtxt) return rtxt;
        return rest as IMessageChannel;
    }

    public static async Task EnsureThreadOpenAsync(IMessageChannel chan)
    {
        if (chan is SocketThreadChannel st && st.IsArchived)
            await st.ModifyAsync(p => p.Archived = false);

        if (chan is RestThreadChannel rt && rt.IsArchived)
            await rt.ModifyAsync(p => p.Archived = false);
    }

    public static string ReadReqString(object req, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            try
            {
                var prop = req.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                var value = prop?.GetValue(req)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch { }
        }

        return "";
    }

    public static ulong ReadReqUlong(object req, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var raw = ReadReqString(req, name);
            if (ulong.TryParse(raw, out var id) && id != 0)
                return id;
        }

        return 0;
    }

    public static string NormalizeDisplayName(string? requested, string? discordUsername)
    {
        var dn = (requested ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(dn) &&
            !dn.Equals("User", StringComparison.OrdinalIgnoreCase))
            return dn;

        var du = (discordUsername ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(du))
            return du;

        return string.IsNullOrWhiteSpace(dn) ? "User" : dn;
    }

    public static async Task<ulong> ResolveTargetDriverUserIdAsync(SocketGuild guild, SendMessageReq payload)
    {
        // strongest: explicit recipient fields
        var explicitId = ReadReqUlong(payload,
            "DriverDiscordUserId",
            "RecipientDiscordUserId",
            "ToDiscordUserId");

        if (explicitId != 0)
            return explicitId;

        // weaker: generic Discord user id only if caller is clearly targeting a driver
        var genericId = ReadReqUlong(payload, "DiscordUserId");
        var to = ReadReqString(payload, "To");
        var recipient = ReadReqString(payload, "Recipient");

        var routeToDispatch =
            string.Equals(to, "dispatch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(recipient, "dispatch", StringComparison.OrdinalIgnoreCase);

        if (!routeToDispatch && genericId != 0)
            return genericId;

        // fallback: name matching
        var targetName = FirstNonEmpty(
            ReadReqString(payload, "Recipient"),
            ReadReqString(payload, "To"),
            ReadReqString(payload, "DriverName"),
            ReadReqString(payload, "RecipientDiscordUsername"),
            ReadReqString(payload, "DriverDiscordUsername"),
            ReadReqString(payload, "DiscordUsername"));

        if (string.IsNullOrWhiteSpace(targetName))
            return 0;

        try { await guild.DownloadUsersAsync(); } catch { }

        var user = guild.Users.FirstOrDefault(u =>
            string.Equals((u.Username ?? "").Trim(), targetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals((u.Nickname ?? "").Trim(), targetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals((u.GlobalName ?? "").Trim(), targetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals((u.DisplayName ?? "").Trim(), targetName, StringComparison.OrdinalIgnoreCase));

        return user?.Id ?? 0;
    }

    public static string ResolveDriverDisplayName(SocketGuild guild, ulong userId, SendMessageReq payload)
    {
        var user = guild.GetUser(userId);
        if (user != null)
        {
            var display = FirstNonEmpty(
                user.Nickname,
                user.GlobalName,
                user.DisplayName,
                user.Username);

            if (!string.IsNullOrWhiteSpace(display))
                return display;
        }

        return FirstNonEmpty(
            ReadReqString(payload, "Recipient"),
            ReadReqString(payload, "To"),
            ReadReqString(payload, "DriverName"),
            ReadReqString(payload, "RecipientDiscordUsername"),
            ReadReqString(payload, "DriverDiscordUsername"),
            ReadReqString(payload, "DiscordUsername"),
            "Driver");
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return "";
    }
}
