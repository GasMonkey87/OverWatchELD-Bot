using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Threads
{
    /// <summary>
    /// Option A: auto-thread per (guildId, discordUserId) under that guild's dispatch channel.
    /// Option B: manual override via !linkthread stores mapping in ThreadMapStore.
    /// </summary>
    public sealed class DiscordThreadRouter
    {
        private readonly DiscordSocketClient _client;
        private readonly ThreadMapStore _store;
        private readonly ulong _dispatchChannelId;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public DiscordThreadRouter(DiscordSocketClient client, ThreadMapStore store, ulong dispatchChannelId)
        {
            _client = client;
            _store = store;
            _dispatchChannelId = dispatchChannelId;
        }

        public async Task<ulong> GetOrCreateThreadIdAsync(string guildId, string discordUserId)
        {
            guildId = (guildId ?? "").Trim();
            discordUserId = (discordUserId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                throw new ArgumentException("guildId is required", nameof(guildId));
            if (string.IsNullOrWhiteSpace(discordUserId))
                throw new ArgumentException("discordUserId is required", nameof(discordUserId));

            // Fast path: existing mapping
            if (_store.TryGetThread(guildId, discordUserId, out var existingId))
            {
                if (_client.GetChannel(existingId) != null)
                    return existingId;

                // Thread deleted; remove mapping
                _store.Remove(guildId, discordUserId);
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_store.TryGetThread(guildId, discordUserId, out existingId) && _client.GetChannel(existingId) != null)
                    return existingId;

                var parent = _client.GetChannel(_dispatchChannelId) as SocketTextChannel;
                if (parent == null)
                    throw new InvalidOperationException("Dispatch channel not found or not a text channel.");

                // ✅ Public-release safe: no real Discord name in thread title
                var suffix = discordUserId.Length >= 6 ? discordUserId[^6..] : discordUserId;
                var threadName = $"dispatch-user-{suffix}";
                if (threadName.Length > 100) threadName = threadName.Substring(0, 100);

                // Prefer private thread to keep it clean.
                var thread = await parent.CreateThreadAsync(
                    name: threadName,
                    autoArchiveDuration: ThreadArchiveDuration.OneDay,
                    type: ThreadType.PrivateThread
                ).ConfigureAwait(false);

                // Invite the user to the private thread when possible
                try
                {
                    if (ulong.TryParse(discordUserId, out var uid))
                    {
                        var u = _client.GetUser(uid);
                        if (u is SocketGuildUser gu)
                        {
                            try { await thread.AddUserAsync(gu).ConfigureAwait(false); } catch { }
                        }
                    }
                }
                catch { }

                _store.SetThread(guildId, discordUserId, thread.Id);

                // Small header message is OK (doesn't expose real name)
                try { await thread.SendMessageAsync($"Thread created for <@{discordUserId}>.").ConfigureAwait(false); } catch { }

                return thread.Id;
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task SetThreadOverrideAsync(string guildId, string discordUserId, ulong threadId)
        {
            guildId = (guildId ?? "").Trim();
            discordUserId = (discordUserId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                throw new ArgumentException("guildId is required", nameof(guildId));
            if (string.IsNullOrWhiteSpace(discordUserId))
                throw new ArgumentException("discordUserId is required", nameof(discordUserId));

            _store.SetThread(guildId, discordUserId, threadId);
            return Task.CompletedTask;
        }

        public async Task SendToUserThreadAsync(string guildId, string discordUserId, string text)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            var threadId = await GetOrCreateThreadIdAsync(guildId, discordUserId).ConfigureAwait(false);
            var ch = _client.GetChannel(threadId) as IMessageChannel;
            if (ch == null) throw new InvalidOperationException("Thread channel missing");
            await ch.SendMessageAsync(text).ConfigureAwait(false);
        }
    }
}
