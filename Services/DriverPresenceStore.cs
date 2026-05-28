using System.Collections.Concurrent;

namespace OverWatchELD.VtcBot.Services;

public sealed class DriverPresenceRecord
{
    public string GuildId { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public bool IsOnline { get; set; }
    public string Status { get; set; } = "Offline";
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public static class DriverPresenceStore
{
    private static readonly ConcurrentDictionary<string, DriverPresenceRecord> Items = new();

    private static string Key(string guildId, string discordUserId)
        => $"{guildId}:{discordUserId}";

    public static DriverPresenceRecord Upsert(DriverPresenceRecord record)
    {
        record.LastSeenUtc = DateTime.UtcNow;
        record.Status = record.IsOnline ? "Online" : "Offline";

        Items[Key(record.GuildId, record.DiscordUserId)] = record;
        return record;
    }

    public static List<DriverPresenceRecord> GetGuild(string guildId)
    {
        return Items.Values
            .Where(x => x.GuildId == guildId)
            .OrderByDescending(x => x.LastSeenUtc)
            .ToList();
    }
}
