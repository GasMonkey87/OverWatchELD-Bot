using System;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Threads
{
    /// <summary>
    /// Option A: auto-thread per discordUserId under a fixed dispatch channel.
    /// Option B: manual override via /linkthread stores mapping in ThreadMapStore.
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

        public async Task<ulong> GetOrCreateThreadIdAsync(string discordUserId, string? displayName = null)
        {
            discordUserId = (discordUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(discordUserId))
                throw new ArgumentException("discordUserId is required", nameof(discordUserId));

            // Fast path: existing mapping
            if (_store.TryGetThread(discordUserId, out var existingId))
            {
                if (_client.GetChannel(existingId) != null)
                    return existingId;

                // Thread deleted; remove mapping
                _store.Remove(discordUserId);
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_store.TryGetThread(discordUserId, out existingId) && _client.GetChannel(existingId) != null)
                    return existingId;

                var parent = _client.GetChannel(_dispatchChannelId) as SocketTextChannel;
                if (parent == null)
                    throw new InvalidOperationException("Dispatch channel not found or not a text channel.");

                var safeName = (displayName ?? discordUserId).Trim();
                if (safeName.Length > 32) safeName = safeName.Substring(0, 32);
                var threadName = $"dispatch-{safeName}";
                if (threadName.Length > 100) threadName = threadName.Substring(0, 100);

                // Prefer private thread to keep it clean.
                var thread = await parent.CreateThreadAsync(
                    name: threadName,
                    autoArchiveDuration: ThreadArchiveDuration.OneDay,
                    type: ThreadType.PrivateThread
                ).ConfigureAwait(false);

                // Invite the user to private thread when we can resolve them.
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

                _store.SetThread(discordUserId, thread.Id);

                try { await thread.SendMessageAsync($"Thread created for <@{discordUserId}>.").ConfigureAwait(false); } catch { }

                return thread.Id;
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task SetThreadOverrideAsync(string discordUserId, ulong threadId)
        {
            discordUserId = (discordUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(discordUserId))
                throw new ArgumentException("discordUserId is required", nameof(discordUserId));

            _store.SetThread(discordUserId, threadId);
            return Task.CompletedTask;
        }

        public async Task SendToUserThreadAsync(string discordUserId, string? displayName, string text)
        {
            var threadId = await GetOrCreateThreadIdAsync(discordUserId, displayName).ConfigureAwait(false);
            var ch = _client.GetChannel(threadId) as IMessageChannel;
            if (ch == null) throw new InvalidOperationException("Thread channel missing");
            await ch.SendMessageAsync(text).ConfigureAwait(false);
        }
    }
}
