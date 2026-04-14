using System.Collections.Concurrent;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores;

public sealed class WebSessionStore
{
    private readonly ConcurrentDictionary<string, WebSessionUser> _sessions = new(StringComparer.Ordinal);

    public void Save(string sessionId, WebSessionUser user)
    {
        _sessions[sessionId] = user;
    }

    public bool TryGet(string sessionId, out WebSessionUser? user)
    {
        if (_sessions.TryGetValue(sessionId, out var found))
        {
            if (found.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                user = found;
                return true;
            }

            _sessions.TryRemove(sessionId, out _);
        }

        user = null;
        return false;
    }

    public void Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }
}
