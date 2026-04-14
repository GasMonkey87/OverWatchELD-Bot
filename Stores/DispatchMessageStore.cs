using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores;

public sealed class DispatchMessageStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _readOpts;
    private readonly JsonSerializerOptions _writeOpts;

    public DispatchMessageStore(string path, JsonSerializerOptions readOpts, JsonSerializerOptions writeOpts)
    {
        _path = path;
        _readOpts = readOpts;
        _writeOpts = writeOpts;
    }

    private List<DispatchMessage> LoadAll()
    {
        if (!File.Exists(_path))
            return new List<DispatchMessage>();

        try
        {
            return JsonSerializer.Deserialize<List<DispatchMessage>>(File.ReadAllText(_path), _readOpts)
                   ?? new List<DispatchMessage>();
        }
        catch
        {
            return new List<DispatchMessage>();
        }
    }

    private void SaveAll(List<DispatchMessage> list)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_path, JsonSerializer.Serialize(list, _writeOpts));
    }

    public List<DispatchMessage> List(string guildId)
    {
        return LoadAll()
            .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
    }

    public List<DispatchMessage> ListConversation(string guildId, string driverDiscordUserId)
    {
        return LoadAll()
            .Where(x =>
                string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DriverDiscordUserId, driverDiscordUserId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.CreatedUtc)
            .ToList();
    }

    public List<DispatchMessage> ListRecent(string guildId, int take = 100)
    {
        if (take <= 0)
            take = 100;

        return LoadAll()
            .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedUtc)
            .Take(take)
            .ToList();
    }

    public List<DispatchConversationSummary> ListRecentConversations(string guildId, int take = 50)
    {
        if (take <= 0)
            take = 50;

        var all = LoadAll()
            .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

        var grouped = all
            .GroupBy(x => NormalizeConversationKey(x))
            .Select(g =>
            {
                var ordered = g.OrderByDescending(x => x.CreatedUtc).ToList();
                var latest = ordered.First();
                var unread = g.Count(x => !x.IsRead);

                return new DispatchConversationSummary
                {
                    DriverDiscordUserId = Safe(latest.DriverDiscordUserId),
                    DriverName = FirstNonBlank(
                        latest.DriverName,
                        ordered.Select(x => x.DriverName).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                        "Unknown Driver"),
                    LastMessageText = Safe(latest.Text),
                    LastDirection = Safe(latest.Direction),
                    LastCreatedUtc = latest.CreatedUtc,
                    UnreadCount = unread,
                    MessageCount = g.Count()
                };
            })
            .OrderByDescending(x => x.LastCreatedUtc)
            .Take(take)
            .ToList();

        return grouped;
    }

    public DispatchMessage Add(DispatchMessage message)
    {
        var list = LoadAll();

        if (string.IsNullOrWhiteSpace(message.Id))
            message.Id = Guid.NewGuid().ToString("N");

        if (message.CreatedUtc == default)
            message.CreatedUtc = DateTimeOffset.UtcNow;

        message.GuildId = Safe(message.GuildId);
        message.DriverDiscordUserId = Safe(message.DriverDiscordUserId);
        message.DriverName = Safe(message.DriverName);
        message.Text = Safe(message.Text);
        message.Direction = Safe(message.Direction);

        list.Add(message);
        SaveAll(list);
        return message;
    }

    public int MarkRead(string guildId, string driverDiscordUserId)
    {
        var list = LoadAll();
        var count = 0;

        foreach (var item in list)
        {
            if (string.Equals(item.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.DriverDiscordUserId, driverDiscordUserId, StringComparison.OrdinalIgnoreCase) &&
                !item.IsRead)
            {
                item.IsRead = true;
                count++;
            }
        }

        if (count > 0)
            SaveAll(list);

        return count;
    }

    public int MarkAllRead(string guildId)
    {
        var list = LoadAll();
        var count = 0;

        foreach (var item in list)
        {
            if (string.Equals(item.GuildId, guildId, StringComparison.OrdinalIgnoreCase) && !item.IsRead)
            {
                item.IsRead = true;
                count++;
            }
        }

        if (count > 0)
            SaveAll(list);

        return count;
    }

    public int GetUnreadCount(string guildId)
    {
        return LoadAll().Count(x =>
            string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
            !x.IsRead);
    }

    public int GetUnreadCount(string guildId, string driverDiscordUserId)
    {
        return LoadAll().Count(x =>
            string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.DriverDiscordUserId, driverDiscordUserId, StringComparison.OrdinalIgnoreCase) &&
            !x.IsRead);
    }

    private static string NormalizeConversationKey(DispatchMessage message)
    {
        var driverId = Safe(message.DriverDiscordUserId);
        if (!string.IsNullOrWhiteSpace(driverId))
            return "discord:" + driverId.ToLowerInvariant();

        var driverName = Safe(message.DriverName);
        if (!string.IsNullOrWhiteSpace(driverName))
            return "name:" + driverName.ToLowerInvariant();

        return "unknown";
    }

    private static string Safe(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }
}

public sealed class DispatchConversationSummary
{
    public string DriverDiscordUserId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string LastMessageText { get; set; } = "";
    public string LastDirection { get; set; } = "";
    public DateTimeOffset LastCreatedUtc { get; set; }
    public int UnreadCount { get; set; }
    public int MessageCount { get; set; }
}
