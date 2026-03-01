using System;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Threads
{
    /// <summary>
    /// Option B: run inside an existing thread to override routing.
    /// /linkthread (links yourself)
    /// /linkthread user:@User (links specified user)
    /// </summary>
    public sealed class LinkThreadCommand : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DiscordThreadRouter _router;

        public LinkThreadCommand(DiscordThreadRouter router)
        {
            _router = router;
        }

        [SlashCommand("linkthread", "Link this Discord thread as your ELD dispatch thread (override)")]
        public async Task LinkThread([Summary("user", "Optional: link a different user")] SocketGuildUser? user = null)
        {
            if (Context.Channel is not SocketThreadChannel thread)
            {
                await RespondAsync("Run this command inside a thread.", ephemeral: true);
                return;
            }

            var target = user ?? (Context.User as SocketGuildUser);
            if (target == null)
            {
                await RespondAsync("Unable to resolve user.", ephemeral: true);
                return;
            }

            await _router.SetThreadOverrideAsync(target.Id.ToString(), thread.Id);
            await RespondAsync($"✅ Linked this thread to <@{target.Id}>.", ephemeral: true);
        }
    }
}
