using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Commands
{
    public sealed class WebhookCommands
    {
        private readonly WebhookStore _store;
        private readonly WebhookPoster _poster;

        public WebhookCommands(WebhookStore store, WebhookPoster poster)
        {
            _store = store;
            _poster = poster;
        }

        public async Task<bool> TryHandleAsync(SocketMessage raw)
        {
            if (raw is not SocketUserMessage msg) return false;
            if (msg.Author.IsBot) return false;

            // Only respond in guild text channels (admin-only makes sense here)
            if (msg.Channel is not SocketGuildChannel gc) return false;

            var text = msg.Content?.Trim() ?? "";
            if (!text.StartsWith("!webhook", StringComparison.OrdinalIgnoreCase))
                return false;

            // Admin check: Administrator OR ManageGuild
            if (!IsAdmin(msg))
            {
                await msg.Channel.SendMessageAsync("⛔ Admin only.");
                return true;
            }

            // Parse: !webhook <subcmd> ...
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await msg.Channel.SendMessageAsync(
                    "Usage:\n" +
                    "`!webhook add <key> <url>`\n" +
                    "`!webhook remove <key>`\n" +
                    "`!webhook list`\n" +
                    "`!webhook test <key> <message>`");
                return true;
            }

            var sub = parts[1].ToLowerInvariant();
            var guildId = gc.Guild.Id;

            if (sub == "list")
            {
                var hooks = _store.GetGuildWebhooks(guildId);
                if (hooks.Count == 0)
                {
                    await msg.Channel.SendMessageAsync("No webhooks saved for this server yet.");
                    return true;
                }

                // Mask URL a bit for safety
                var lines = hooks
                    .OrderBy(k => k.Key)
                    .Select(kvp => $"- **{kvp.Key}** = `{Mask(kvp.Value)}`");

                await msg.Channel.SendMessageAsync("Saved webhooks:\n" + string.Join("\n", lines));
                return true;
            }

            if (sub == "add")
            {
                if (parts.Length < 4)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!webhook add <key> <url>`");
                    return true;
                }

                var key = parts[2];
                var url = parts[3];

                if (!IsProbablyDiscordWebhook(url))
                {
                    await msg.Channel.SendMessageAsync("That doesn't look like a Discord webhook URL.");
                    return true;
                }

                _store.Set(guildId, key, url);
                await msg.Channel.SendMessageAsync($"✅ Saved webhook **{key}**.");
                return true;
            }

            if (sub == "remove" || sub == "delete")
            {
                if (parts.Length < 3)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!webhook remove <key>`");
                    return true;
                }

                var key = parts[2];
                var ok = _store.Remove(guildId, key);
                await msg.Channel.SendMessageAsync(ok ? $"✅ Removed **{key}**." : $"No webhook named **{key}** found.");
                return true;
            }

            if (sub == "test")
            {
                if (parts.Length < 4)
                {
                    await msg.Channel.SendMessageAsync("Usage: `!webhook test <key> <message>`");
                    return true;
                }

                var key = parts[2];
                var message = string.Join(' ', parts.Skip(3));

                if (!_store.TryGet(guildId, key, out var url))
                {
                    await msg.Channel.SendMessageAsync($"No webhook named **{key}** found.");
                    return true;
                }

                var ok = await _poster.PostAsync(url, message);
                await msg.Channel.SendMessageAsync(ok ? $"✅ Sent test message to **{key}**." : $"❌ Failed to post to **{key}**.");
                return true;
            }

            await msg.Channel.SendMessageAsync("Unknown subcommand. Try `!webhook list`.");
            return true;
        }

        private static bool IsAdmin(SocketUserMessage msg)
        {
            if (msg.Author is not SocketGuildUser gu) return false;
            var perms = gu.GuildPermissions;
            return perms.Administrator || perms.ManageGuild;
        }

        private static bool IsProbablyDiscordWebhook(string url)
        {
            // Simple sanity check (don’t overdo it)
            return url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://ptb.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://canary.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Mask(string url)
        {
            // Show beginning + last 6 chars
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (url.Length <= 18) return url;
            return url.Substring(0, 12) + "..." + url.Substring(Math.Max(0, url.Length - 6));
        }
    }
}
