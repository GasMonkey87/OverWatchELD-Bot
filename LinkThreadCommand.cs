using System;
using System.Linq;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Threads
{
    public static class LinkThreadCommand
    {
        /// <summary>
        /// Option B: manual override.
        /// Run inside a thread: !linkthread OR !linkthread @User
        /// </summary>
        public static async Task<bool> TryHandleAsync(SocketUserMessage msg, DiscordSocketClient client, ThreadMapStore store)
        {
            var content = (msg.Content ?? "").Trim();
            if (!content.StartsWith("!linkthread", StringComparison.OrdinalIgnoreCase))
                return false;

            if (msg.Channel is not SocketThreadChannel thread)
            {
                await msg.Channel.SendMessageAsync("Run **!linkthread** inside a thread.");
                return true;
            }

            var guild = thread.Guild;
            if (guild == null)
            {
                await msg.Channel.SendMessageAsync("❌ Could not resolve guild.");
                return true;
            }

            var target = msg.MentionedUsers.FirstOrDefault();
            var targetUserId = (target?.Id ?? msg.Author.Id).ToString();

            var key = $"{guild.Id}:{targetUserId}";
            store.SetThread(key, thread.Id);

            await msg.Channel.SendMessageAsync($"✅ Linked this thread to <@{targetUserId}> for ELD routing.");
            return true;
        }
    }
}
