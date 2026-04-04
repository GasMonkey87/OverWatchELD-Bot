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

    public DispatchMessage Add(DispatchMessage message)
    {
        var list = LoadAll();
        if (string.IsNullOrWhiteSpace(message.Id))
            message.Id = Guid.NewGuid().ToString("N");
        if (message.CreatedUtc == default)
            message.CreatedUtc = DateTimeOffset.UtcNow;
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
}
