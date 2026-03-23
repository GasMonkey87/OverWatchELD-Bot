using System.Security.Cryptography;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;
using OverWatchELD.VtcBot.Threads;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Commands;

public static class BotCommandHandler
{
    private static readonly object _rateLock = new();
    private static readonly Dictionary<string, DateTimeOffset> _lastCommandByUser = new(StringComparer.OrdinalIgnoreCase);

    public static async Task HandleMessageAsync(SocketMessage socketMsg, BotServices services)
    {
        var client = services.Client;
        if (client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;

        try
        {
            if (client.CurrentUser != null && msg.Author.Id == client.CurrentUser.Id)
                return;
        }
        catch { }

        try
        {
            var now = DateTime.UtcNow;
            lock (services.HandledMessageLock)
            {
                var expired = services.HandledMessages
                    .Where(x => (now - x.Value).TotalMinutes > 10)
                    .Select(x => x.Key)
                    .ToList();

                foreach (var key in expired)
                    services.HandledMessages.Remove(key);

                if (services.HandledMessages.ContainsKey(msg.Id))
                    return;

                services.HandledMessages[msg.Id] = now;
            }
        }
        catch { }

        if (services.ThreadStore != null)
        {
            try
            {
                var handled = await LinkThreadCommand.TryHandleAsync(msg, client, services.ThreadStore);
                if (handled) return;
            }
            catch { }
        }

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        var ctx = Parse(msg, content);

        if (!PassRateLimit(ctx))
        {
            await ctx.Message.Channel.SendMessageAsync("⏳ Slow down a sec.");
            return;
        }

        if (ctx.Content.Equals("!ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (ctx.Cmd == "link")
        {
            await HandleLinkAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "setdispatchwebhook")
        {
            await HandleSetDispatchWebhookAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "rosterlink")
        {
            await HandleRosterLinkAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "rosterlist")
        {
            await HandleRosterListAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "announcement" || ctx.Cmd == "announcements")
        {
            await HandleAnnouncementAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "setannouncementwebhook")
        {
            await HandleSetAnnouncementWebhookAsync(ctx, services);
            return;
        }

        await msg.Channel.SendMessageAsync("Unknown command. Use `!help`.");
    }

    private static CommandContext Parse(SocketUserMessage msg, string content)
    {
        var parts = content.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var cmd0 = parts.Length > 0 ? parts[0].TrimStart('!').Trim().ToLowerInvariant() : "";
        var arg0 = parts.Length > 1 ? parts[1].Trim() : "";
        var arg1 = parts.Length > 2 ? parts[2].Trim() : "";
        var arg = parts.Length > 1 ? content[(content.IndexOf(' ') + 1)..].Trim() : "";

        SocketGuild? guild = null;
        string guildIdStr = "";

        if (msg.Channel is SocketGuildChannel guildChannel)
        {
            guild = guildChannel.Guild;
            guildIdStr = guild.Id.ToString();
        }

        return new CommandContext
        {
            Message = msg,
            Content = content,
            Cmd = cmd0,
            Arg = arg,
            Arg0 = arg0,
            Arg1 = arg1,
            Guild = guild,
            GuildIdStr = guildIdStr
        };
    }

    private static bool PassRateLimit(CommandContext ctx)
    {
        var userKey = $"{ctx.GuildIdStr}:{ctx.Message.Author.Id}";
        var now = DateTimeOffset.UtcNow;

        lock (_rateLock)
        {
            var expired = _lastCommandByUser
                .Where(x => (now - x.Value).TotalMinutes > 5)
                .Select(x => x.Key)
                .ToList();

            foreach (var key in expired)
                _lastCommandByUser.Remove(key);

            if (_lastCommandByUser.TryGetValue(userKey, out var last))
            {
                if ((now - last).TotalSeconds < 2.5)
                    return false;
            }

            _lastCommandByUser[userKey] = now;
            return true;
        }
    }

    private static bool UserHasStaffRole(CommandContext ctx)
    {
        if (ctx.Guild == null) return false;
        if (ctx.Message.Author.Id == ctx.Guild.OwnerId) return true;
        if (ctx.Message.Author is not SocketGuildUser gu) return false;

        return gu.Roles.Any(r =>
            r.Name.Equals("Owner", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals("Dispatcher", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> RequireStaffAsync(CommandContext ctx)
    {
        if (UserHasStaffRole(ctx)) return true;

        await ctx.Message.Channel.SendMessageAsync("⛔ This command is restricted to Owner/Admin/Manager/Dispatcher roles.");
        return false;
    }

    private static async Task HandleLinkAsync(CommandContext ctx, BotServices services)
    {
        if (services.LinkCodeStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Link store not ready.");
            return;
        }

        if (ctx.Message.Channel is not SocketGuildChannel gch)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Run `!link` inside your VTC server (not DM), then paste the code into the ELD.");
            return;
        }

        try
        {
            var guild = gch.Guild;
            var code = (ctx.Arg0 ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                code = GenerateLinkCode(6);

            var entry = new LinkCodeEntry
            {
                Code = code,
                GuildId = guild.Id.ToString(),
                GuildName = guild.Name ?? "",
                DiscordUserId = ctx.Message.Author.Id.ToString(),
                DiscordUsername = (ctx.Message.Author.Username ?? "").Trim(),
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
            };

            services.LinkCodeStore.Put(entry);

            // ✅ full finish: save linked history candidate immediately
            try
            {
                services.LinkedDriversStore?.Link(
                    guild.Id.ToString(),
                    ctx.Message.Author.Id.ToString(),
                    (ctx.Message.Author.Username ?? "").Trim(),
                    code);
            }
            catch { }

            // ✅ full finish: ensure roster reflects the Discord user
            try
            {
                if (services.RosterStore != null)
                {
                    var guildUser = guild.GetUser(ctx.Message.Author.Id);
                    var driverName = (guildUser?.DisplayName ?? guildUser?.Username ?? ctx.Message.Author.Username ?? "Driver").Trim();

                    services.RosterStore.AddOrUpdateByName(guild.Id.ToString(), new VtcDriver
                    {
                        Name = driverName,
                        DiscordUserId = ctx.Message.Author.Id.ToString(),
                        DiscordUsername = (ctx.Message.Author.Username ?? "").Trim(),
                        Role = "Driver",
                        Status = "Linked"
                    });
                }
            }
            catch { }

            await ctx.Message.Channel.SendMessageAsync(
                $"🔗 **ELD Link Code:** `{code}`\n" +
                $"Paste this into the ELD within **10 minutes**.\n" +
                $"Driver: <@{ctx.Message.Author.Id}>");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LINK] Error: {ex}");
            await ctx.Message.Channel.SendMessageAsync("❌ Failed to create link code.");
        }
    }

    private static async Task HandleSetDispatchWebhookAsync(CommandContext ctx, BotServices services)
    {
        if (!await RequireStaffAsync(ctx)) return;

        if (services.DispatchStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Dispatch store not initialized.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ctx.Arg) || !ctx.Arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.Message.Channel.SendMessageAsync("Usage: `!setdispatchwebhook https://discord.com/api/webhooks/...`");
            return;
        }

        services.DispatchStore.SetDispatchWebhook(ctx.GuildIdStr, ctx.Arg.Trim());
        await ctx.Message.Channel.SendMessageAsync("✅ Dispatch webhook saved.");
    }

    private static async Task HandleRosterLinkAsync(CommandContext ctx, BotServices services)
    {
        if (!await RequireStaffAsync(ctx)) return;

        if (services.RosterStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Roster store not initialized.");
            return;
        }

        if (ctx.Guild == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ This command must be used in a server.");
            return;
        }

        var parts = (ctx.Arg ?? "").Split('|', 2, StringSplitOptions.RemoveEmptyEntries);
        var left = (parts.Length > 0 ? parts[0] : "").Trim();
        var right = (parts.Length > 1 ? parts[1] : "").Trim();

        var uid = RosterMerge.TryParseUserIdFromMentionOrId(left);
        if (uid == null || uid.Value == 0)
        {
            await ctx.Message.Channel.SendMessageAsync("Usage: `!rosterLink @user | DriverName`");
            return;
        }

        var u = ctx.Guild.GetUser(uid.Value);
        var driverName = !string.IsNullOrWhiteSpace(right)
            ? right
            : ((u?.DisplayName ?? u?.Username ?? "Driver").Trim());

        if (string.IsNullOrWhiteSpace(driverName))
        {
            await ctx.Message.Channel.SendMessageAsync("❌ DriverName is required.");
            return;
        }

        try
        {
            var saved = services.RosterStore.AddOrUpdateByName(ctx.GuildIdStr, new VtcDriver
            {
                Name = driverName.Trim(),
                DiscordUserId = uid.Value.ToString(),
                DiscordUsername = (u?.Username ?? "").Trim(),
                Role = "Driver"
            });

            await ctx.Message.Channel.SendMessageAsync($"✅ Roster linked: **{saved.Name}** ↔ <@{uid.Value}>");
        }
        catch (Exception ex)
        {
            await ctx.Message.Channel.SendMessageAsync($"❌ Roster link failed: {ex.Message}");
        }
    }

    private static async Task HandleRosterListAsync(CommandContext ctx, BotServices services)
    {
        if (!await RequireStaffAsync(ctx)) return;

        if (services.RosterStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Roster store not initialized.");
            return;
        }

        var list = services.RosterStore.List(ctx.GuildIdStr)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        if (list.Count == 0)
        {
            await ctx.Message.Channel.SendMessageAsync("📋 Roster is empty. Use `!rosterLink @user | DriverName`");
            return;
        }

        var lines = new List<string> { "📋 **VTC Roster (top 30)**" };
        foreach (var d in list)
        {
            var link = !string.IsNullOrWhiteSpace(d.DiscordUserId) && ulong.TryParse(d.DiscordUserId, out var id)
                ? $"<@{id}>"
                : "(unlinked)";

            var extra = string.Join(" • ", new[]
            {
                string.IsNullOrWhiteSpace(d.TruckNumber) ? null : $"Truck {d.TruckNumber}",
                string.IsNullOrWhiteSpace(d.Role) ? null : d.Role,
                string.IsNullOrWhiteSpace(d.Status) ? null : d.Status
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            lines.Add($"• **{d.Name}** — {link}" + (string.IsNullOrWhiteSpace(extra) ? "" : $" — {extra}"));
        }

        var textOut = string.Join("\n", lines);
        await ctx.Message.Channel.SendMessageAsync(textOut.Length > 1800 ? textOut[..1800] + "\n..." : textOut);
    }

    private static async Task HandleAnnouncementAsync(CommandContext ctx, BotServices services)
    {
        if (!await RequireStaffAsync(ctx)) return;

        if (services.DispatchStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Dispatch store not initialized.");
            return;
        }

        if (ctx.Guild == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ This command must be used in a server.");
            return;
        }

        var cid = TryParseChannelIdFromMention(ctx.Arg);
        if (cid == null)
        {
            await ctx.Message.Channel.SendMessageAsync("Usage: `!announcement #announcements`");
            return;
        }

        var ch = ctx.Guild.GetTextChannel(cid.Value);
        if (ch == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Must be a text channel.");
            return;
        }

        try
        {
            var hook = await ch.CreateWebhookAsync("OverWatchELD Announcements");
            var url = BuildWebhookUrl(hook);

            services.DispatchStore.SetAnnouncementChannel(ctx.GuildIdStr, ch.Id);

            if (string.IsNullOrWhiteSpace(url))
            {
                await ctx.Message.Channel.SendMessageAsync("✅ Channel set. Webhook token missing; copy URL in Discord and run `!setannouncementwebhook <url>`");
                return;
            }

            services.DispatchStore.SetAnnouncementWebhook(ctx.GuildIdStr, url);
            await ctx.Message.Channel.SendMessageAsync($"✅ Announcements linked: <#{ch.Id}>");
        }
        catch (Exception ex)
        {
            await ctx.Message.Channel.SendMessageAsync($"❌ Webhook create failed (need Manage Webhooks). {ex.Message}");
        }
    }

    private static async Task HandleSetAnnouncementWebhookAsync(CommandContext ctx, BotServices services)
    {
        if (!await RequireStaffAsync(ctx)) return;

        if (services.DispatchStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Dispatch store not initialized.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ctx.Arg) || !ctx.Arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.Message.Channel.SendMessageAsync("Usage: `!setannouncementwebhook https://discord.com/api/webhooks/...`");
            return;
        }

        services.DispatchStore.SetAnnouncementWebhook(ctx.GuildIdStr, ctx.Arg.Trim());
        await ctx.Message.Channel.SendMessageAsync("✅ Announcement webhook saved.");
    }

    public static string GenerateLinkCode(int len)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        len = Math.Clamp(len, 4, 12);

        var bytes = new byte[len];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];

        return new string(chars);
    }

    public static ulong? TryParseChannelIdFromMention(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("<#") && raw.EndsWith(">"))
            raw = raw.Substring(2, raw.Length - 3);
        return ulong.TryParse(raw, out var id) ? id : null;
    }

    public static string? BuildWebhookUrl(RestWebhook hook)
    {
        try
        {
            var token = (hook.Token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token)) return null;
            return $"https://discord.com/api/webhooks/{hook.Id}/{token}";
        }
        catch
        {
            return null;
        }
    }
}
