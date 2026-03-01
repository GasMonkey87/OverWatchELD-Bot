using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Threads
{
    /// <summary>
    /// Option A: Auto-thread per (guildId, discordUserId) under configured dispatch channel.
    /// Uses ThreadMapStore with key "{guildId}:{discordUserId}".
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

        private static string Key(string guildId, string discordUserId)
            => $"{(guildId ?? "").Trim()}:{(discordUserId ?? "").Trim()}";

        public async Task<ulong> GetOrCreateThreadIdAsync(string guildId, string discordUserId)
        {
            guildId = (guildId ?? "").Trim();
            discordUserId = (discordUserId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId))
                throw new ArgumentException("guildId is required", nameof(guildId));
            if (string.IsNullOrWhiteSpace(discordUserId))
                throw new ArgumentException("discordUserId is required", nameof(discordUserId));

            var key = Key(guildId, discordUserId);

            // Fast path: existing mapping
            if (_store.TryGetThread(key, out var existingThreadId))
            {
                if (_client.GetChannel(existingThreadId) != null)
                    return existingThreadId;

                // Thread removed; clear mapping
                _store.Remove(key);
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double check after lock
                if (_store.TryGetThread(key, out existingThreadId) && _client.GetChannel(existingThreadId) != null)
                    return existingThreadId;

                var parent = _client.GetChannel(_dispatchChannelId) as SocketTextChannel;
                if (parent == null)
                    throw new InvalidOperationException("Dispatch channel not found or not a text channel.");

                // ✅ Public-safe: do NOT use real Discord username in thread title
                var suffix = discordUserId.Length >= 6 ? discordUserId[^6..] : discordUserId;
                var threadName = $"dispatch-user-{suffix}";
                if (threadName.Length > 100) threadName = threadName.Substring(0, 100);

                var thread = await parent.CreateThreadAsync(
                    name: threadName,
                    autoArchiveDuration: ThreadArchiveDuration.OneDay,
                    type: ThreadType.PrivateThread
                ).ConfigureAwait(false);

                // Attempt to invite the user (safe no-op if intents/permissions prevent it)
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

                _store.SetThread(key, thread.Id);

                // Minimal header is OK
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
            var key = Key(guildId, discordUserId);
            _store.SetThread(key, threadId);
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
