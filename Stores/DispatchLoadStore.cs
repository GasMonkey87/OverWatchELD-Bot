using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores;

public sealed class DispatchLoadStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _readOpts;
    private readonly JsonSerializerOptions _writeOpts;

    public DispatchLoadStore(string path, JsonSerializerOptions readOpts, JsonSerializerOptions writeOpts)
    {
        _path = path;
        _readOpts = readOpts;
        _writeOpts = writeOpts;
    }

    private List<DispatchLoad> LoadAll()
    {
        if (!File.Exists(_path))
            return new List<DispatchLoad>();

        try
        {
            return JsonSerializer.Deserialize<List<DispatchLoad>>(File.ReadAllText(_path), _readOpts)
                   ?? new List<DispatchLoad>();
        }
        catch
        {
            return new List<DispatchLoad>();
        }
    }

    private void SaveAll(List<DispatchLoad> list)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_path, JsonSerializer.Serialize(list, _writeOpts));
    }

    public List<DispatchLoad> List(string guildId)
    {
        return LoadAll()
            .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.UpdatedUtc)
            .ToList();
    }

    public DispatchLoad? GetById(string guildId, string id)
    {
        return LoadAll().FirstOrDefault(x =>
            string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public DispatchLoad Upsert(DispatchLoad load)
    {
        var list = LoadAll();
        var existing = list.FirstOrDefault(x =>
            string.Equals(x.GuildId, load.GuildId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Id, load.Id, StringComparison.OrdinalIgnoreCase));

        load.UpdatedUtc = DateTimeOffset.UtcNow;

        if (existing == null)
        {
            if (string.IsNullOrWhiteSpace(load.Id))
                load.Id = Guid.NewGuid().ToString("N");
            if (load.CreatedUtc == default)
                load.CreatedUtc = DateTimeOffset.UtcNow;
            list.Add(load);
        }
        else
        {
            existing.LoadNumber = load.LoadNumber;
            existing.Status = load.Status;
            existing.Priority = load.Priority;
            existing.Commodity = load.Commodity;
            existing.PickupLocation = load.PickupLocation;
            existing.DropoffLocation = load.DropoffLocation;
            existing.DriverDiscordUserId = load.DriverDiscordUserId;
            existing.DriverName = load.DriverName;
            existing.TruckId = load.TruckId;
            existing.DispatcherNotes = load.DispatcherNotes;
            existing.BolNumber = load.BolNumber;
            existing.AssignedUtc = load.AssignedUtc;
            existing.PickupUtc = load.PickupUtc;
            existing.DeliveredUtc = load.DeliveredUtc;
            existing.DueUtc = load.DueUtc;
            existing.UpdatedUtc = load.UpdatedUtc;
            load = existing;
        }

        SaveAll(list);
        return load;
    }
}
