using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Linq;
using System.Threading.Tasks;
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

        if (ctx.Cmd == "help" || ctx.Cmd == "commands")
        {
            await HandleHelpAsync(ctx);
            return;
        }

        if (ctx.Cmd == "link")
        {
            await HandleLinkAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "unlink")
        {
            await HandleUnlinkAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "setdispatchwebhook")
        {
            await HandleSetDispatchWebhookAsync(ctx, services);
            return;
        }

        if (ctx.Cmd == "exportlogs")
        {
            await HandleExportLogsAsync(ctx);
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

        if (ctx.Cmd == "setbolchannel")
        {
            await HandleSetBolChannelAsync(ctx, services);
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
        if (ctx.Guild == null)
            return false;

        if (ctx.Guild.OwnerId == ctx.Message.Author.Id)
            return true;

        var user = ctx.Guild.GetUser(ctx.Message.Author.Id);
        if (user == null)
            return false;

        if (user.GuildPermissions.Administrator ||
            user.GuildPermissions.ManageGuild ||
            user.GuildPermissions.ManageChannels)
            return true;

        return user.Roles.Any(r =>
        {
            var name = (r.Name ?? "").Trim().ToLowerInvariant();

            return name.Contains("owner") ||
                   name.Contains("admin") ||
                   name.Contains("administrator") ||
                   name.Contains("manager") ||
                   name.Contains("management") ||
                   name.Contains("dispatcher");
        });
    }

    private static async Task<bool> RequireStaffAsync(CommandContext ctx)
    {
        if (UserHasStaffRole(ctx))
            return true;

        await ctx.Message.Channel.SendMessageAsync("⛔ This command is restricted to Owner/Admin/Manager/Dispatcher roles.");
        return false;
    }

    private static async Task HandleHelpAsync(CommandContext ctx)
    {
        await ctx.Message.Channel.SendMessageAsync(
            "**OverWatch ELD Commands**\n" +
            "`!link` - Generate an ELD link code\n" +
            "`!unlink` - Unlink your Discord account from this VTC\n" +
            "`!ping` - Check bot response\n" +
            "`!rosterlist` - Show roster list\n" +
            "`!rosterlink @user | DriverName` - Link a Discord user to roster\n" +
            "`!announcement #channel` - Set announcement channel\n" +
            "`!setannouncementwebhook <url>` - Set announcement webhook\n" +
            "`!setdispatchwebhook <url>` - Set dispatch webhook\n" +
            "`!setbolchannel` - Set this channel as the BOL upload channel");
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
            await ctx.Message.Channel.SendMessageAsync("❌ Run `!link` inside your VTC server, not DM.");
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

            try
            {
                services.LinkedDriversStore?.Link(
                    guild.Id.ToString(),
                    ctx.Message.Author.Id.ToString(),
                    (ctx.Message.Author.Username ?? "").Trim(),
                    code);
            }
            catch { }

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

    private static async Task HandleExportLogsAsync(CommandContext ctx)
    {
        try
        {
            var guildChannel = ctx.Message.Channel as SocketGuildChannel;
            if (guildChannel == null)
            {
                await ctx.Message.Channel.SendMessageAsync("❌ This command can only be used in a server.");
                return;
            }

            var guild = guildChannel.Guild;
            var mentionedUser = ctx.Message.MentionedUsers.FirstOrDefault();
            var targetUser = mentionedUser ?? ctx.Message.Author;
            var date = DateTime.UtcNow.Date;

            var parts = ctx.Message.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (DateTime.TryParse(part, out var parsed))
                {
                    date = parsed.Date;
                    break;
                }
            }

            var exportChannel = guild.TextChannels
                .FirstOrDefault(c => c.Name.Equals("logs-export", StringComparison.OrdinalIgnoreCase));

            if (exportChannel == null)
            {
                await guild.CreateTextChannelAsync("logs-export");
                await ctx.Message.Channel.SendMessageAsync("✅ Created #logs-export. Run `!exportlogs` again.");
                return;
            }

            var threadName = $"logs-{targetUser.Username}-{date:yyyy-MM-dd}"
                .ToLowerInvariant()
                .Replace(" ", "-");

            var existingThread = exportChannel.Threads
                .FirstOrDefault(t => t.Name.Equals(threadName, StringComparison.OrdinalIgnoreCase));

            IThreadChannel thread;

            if (existingThread != null)
            {
                thread = existingThread;
            }
            else
            {
                thread = await exportChannel.CreateThreadAsync(
                    name: threadName,
                    type: ThreadType.PublicThread,
                    autoArchiveDuration: ThreadArchiveDuration.OneWeek);
            }

            var exportText =
$"""
📋 **Daily Log Export**
Driver: {targetUser.Mention}
Date: {date:yyyy-MM-dd}

Status: Export request received.

⚠️ Log data source still needs to be connected here:
- duty events
- violations
- certification status
- truck info
- locations
""";

            await thread.SendMessageAsync(exportText);
            await ctx.Message.Channel.SendMessageAsync($"✅ Logs exported to thread: {thread.Mention}");
        }
        catch (Exception ex)
        {
            await ctx.Message.Channel.SendMessageAsync($"❌ Export failed: `{ex.Message}`");
        }
    }

    private static async Task HandleUnlinkAsync(CommandContext ctx, BotServices services)
    {
        if (ctx.Message.Channel is not SocketGuildChannel)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Run `!unlink` inside your VTC server, not in DM.");
            return;
        }

        var guildId = ctx.GuildIdStr;
        var userId = ctx.Message.Author.Id.ToString();

        if (string.IsNullOrWhiteSpace(guildId))
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Could not detect server.");
            return;
        }

        var removedAnything = false;

        try
        {
            removedAnything |= TryInvokeStoreUnlink(services.LinkCodeStore, guildId, userId);
        }
        catch { }

        try
        {
            removedAnything |= TryInvokeStoreUnlink(services.LinkedDriversStore, guildId, userId);
        }
        catch { }

        try
        {
            if (services.RosterStore != null)
            {
                var guildUser = ctx.Guild?.GetUser(ctx.Message.Author.Id);
                var driverName = (guildUser?.DisplayName ?? guildUser?.Username ?? ctx.Message.Author.Username ?? "Driver").Trim();

                services.RosterStore.AddOrUpdateByName(guildId, new VtcDriver
                {
                    Name = driverName,
                    DiscordUserId = userId,
                    DiscordUsername = (ctx.Message.Author.Username ?? "").Trim(),
                    Role = "Driver",
                    Status = "Unlinked"
                });

                removedAnything = true;
            }
        }
        catch { }

        await ctx.Message.Channel.SendMessageAsync(
            "✅ You have been unlinked from this VTC. Use `!link` again to reconnect your ELD.");

        Console.WriteLine($"[UNLINK] Guild={guildId} User={userId} RemovedAnything={removedAnything}");
    }

    private static bool TryInvokeStoreUnlink(object? store, string guildId, string discordUserId)
    {
        if (store == null) return false;

        var type = store.GetType();

        var methodNames = new[]
        {
            "UnlinkUser",
            "Unlink",
            "RemoveUser",
            "Remove",
            "DeleteUser",
            "Delete",
            "ClearUser"
        };

        foreach (var methodName in methodNames)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var method in methods)
            {
                var p = method.GetParameters();

                try
                {
                    if (p.Length == 2 &&
                        p[0].ParameterType == typeof(string) &&
                        p[1].ParameterType == typeof(string))
                    {
                        method.Invoke(store, new object[] { guildId, discordUserId });
                        return true;
                    }

                    if (p.Length == 1 &&
                        p[0].ParameterType == typeof(string))
                    {
                        method.Invoke(store, new object[] { discordUserId });
                        return true;
                    }
                }
                catch { }
            }
        }

        return false;
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
            await ctx.Message.Channel.SendMessageAsync($"❌ Webhook create failed. Bot needs Manage Webhooks. {ex.Message}");
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

    private static async Task HandleSetBolChannelAsync(CommandContext ctx, BotServices services)
    {
        if (ctx.Guild == null || string.IsNullOrWhiteSpace(ctx.GuildIdStr))
        {
            await ctx.Message.Channel.SendMessageAsync("❌ This command must be used in a server.");
            return;
        }

        if (services.GuildSettingsStore == null)
        {
            await ctx.Message.Channel.SendMessageAsync("❌ Guild settings store not initialized.");
            return;
        }

        try
        {
            var channelId = ctx.Message.Channel.Id;

            await services.GuildSettingsStore.SetBolChannelAsync(ctx.GuildIdStr, channelId);

            await ctx.Message.Channel.SendMessageAsync(
                $"✅ ELD-BOL channel linked.\nBOL messages will now be sent here: <#{channelId}>");
        }
        catch (Exception ex)
        {
            await ctx.Message.Channel.SendMessageAsync(
                $"❌ Failed to set BOL channel.\n{ex.Message}");
        }
    }

    private static async Task HandleMaintenanceRequestAsync(HttpListenerContext ctx, BotServices services)
{
    try
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        var json = await reader.ReadToEndAsync();

        var body = JsonSerializer.Deserialize<JsonElement>(json);

        string Get(string name)
        {
            if (body.TryGetProperty(name, out var v))
                return v.ToString() ?? "";

            return "";
        }

        bool GetBool(string name)
        {
            if (body.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }

            return false;
        }

        var guildId = Get("guildId");

        if (string.IsNullOrWhiteSpace(guildId))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var requestNumber = Get("requestNumber");

        var embed = new EmbedBuilder()
            .WithTitle($"🔧 Maintenance Request #{requestNumber}")
            .WithColor(new Color(255, 140, 0))
            .AddField("Driver", Get("driverName"), true)
            .AddField("Truck", Get("truck"), true)
            .AddField("Unit #", Get("unitNumber"), true)
            .AddField("Plate", Get("plateNumber"), true)
            .AddField("Location", Get("location"), false)
            .AddField("Issue", Get("currentIssue"), false)
            .AddField("Severity", Get("severity"), true)
            .AddField("Condition", $"{Get("conditionPercent")}% ", true)
            .AddField("DOT Inspection", GetBool("dotInspectionRequested") ? "YES" : "No", true)
            .AddField("Damage Repair", GetBool("damageRepairRequested") ? "YES" : "No", true)
            .AddField("Repair Malfunctions", GetBool("malfunctionRepairRequested") ? "YES" : "No", true)
            .AddField("Other Maintenance", GetBool("otherMaintenanceRequested") ? "YES" : "No", true)
            .AddField("Notes", Get("notes"), false)
            .WithCurrentTimestamp();

        if (!ulong.TryParse(guildId, out var gid))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var guild = services.Client.GetGuild(gid);

        if (guild == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var maintenanceChannel = guild.TextChannels
            .FirstOrDefault(x =>
                x.Name.Contains("maintenance", StringComparison.OrdinalIgnoreCase));

        if (maintenanceChannel == null)
        {
            maintenanceChannel = await guild.CreateTextChannelAsync("maintenance-requests");
        }

        await maintenanceChannel.SendMessageAsync(embed: embed.Build());

        ctx.Response.StatusCode = 200;
    }
    catch
    {
        ctx.Response.StatusCode = 500;
    }
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

            if (string.IsNullOrWhiteSpace(token))
                return null;

            return $"https://discord.com/api/webhooks/{hook.Id}/{token}";
        }
        catch
        {
            return null;
        }
    }
}
