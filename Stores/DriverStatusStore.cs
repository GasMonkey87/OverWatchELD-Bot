using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores;

public sealed class DriverStatusStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _readOpts;
    private readonly JsonSerializerOptions _writeOpts;

    public DriverStatusStore(string path, JsonSerializerOptions readOpts, JsonSerializerOptions writeOpts)
    {
        _path = path;
        _readOpts = readOpts;
        _writeOpts = writeOpts;
    }

    public sealed class DriverStatusEntry
    {
        public string GuildId { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string DutyStatus { get; set; } = "";
        public string Truck { get; set; } = "";
        public string LoadNumber { get; set; } = "";
        public string Location { get; set; } = "";
        public double SpeedMph { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Heading { get; set; }
    }

    private List<DriverStatusEntry> LoadAll()
    {
        if (!File.Exists(_path))
            return new List<DriverStatusEntry>();

        try
        {
            return JsonSerializer.Deserialize<List<DriverStatusEntry>>(File.ReadAllText(_path), _readOpts)
                   ?? new List<DriverStatusEntry>();
        }
        catch
        {
            return new List<DriverStatusEntry>();
        }
    }

    private void SaveAll(List<DriverStatusEntry> list)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_path, JsonSerializer.Serialize(list, _writeOpts));
    }

    public List<DriverStatusEntry> List(string guildId)
    {
        return LoadAll()
            .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastSeenUtc)
            .ToList();
    }

    public void Upsert(DriverStatusEntry entry)
    {
        var list = LoadAll();

        var existing = list.FirstOrDefault(x =>
            string.Equals(x.GuildId, entry.GuildId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.DiscordUserId, entry.DiscordUserId, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            list.Add(entry);
        }
        else
        {
            existing.DriverName = entry.DriverName;
            existing.DutyStatus = entry.DutyStatus;
            existing.Truck = entry.Truck;
            existing.LoadNumber = entry.LoadNumber;
            existing.Location = entry.Location;
            existing.SpeedMph = entry.SpeedMph;
            existing.LastSeenUtc = entry.LastSeenUtc;
        }

        SaveAll(list);
    }
}
