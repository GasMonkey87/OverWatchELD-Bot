using System.Text.Json;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores;

public sealed class DriverAwardStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _readOptions;
    private readonly JsonSerializerOptions _writeOptions;
    private readonly object _gate = new();

    private List<DriverAwardEntry> _items = new();

    public DriverAwardStore(
        string path,
        JsonSerializerOptions? readOptions = null,
        JsonSerializerOptions? writeOptions = null)
    {
        _path = path;
        _readOptions = readOptions ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _writeOptions = writeOptions ?? new JsonSerializerOptions { WriteIndented = true };

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        Load();
    }

    public List<DriverAwardEntry> GetForDriver(string guildId, string driverId)
    {
        guildId = (guildId ?? "").Trim();
        driverId = (driverId ?? "").Trim();

        lock (_gate)
        {
            return _items
                .Where(x =>
                    string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.DriverId, driverId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.AwardedUtc)
                .ToList();
        }
    }

    public List<DriverAwardEntry> GetAllForGuild(string guildId)
    {
        guildId = (guildId ?? "").Trim();

        lock (_gate)
        {
            return _items
                .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.AwardedUtc)
                .ToList();
        }
    }

    public DriverAwardEntry Add(DriverAwardEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        entry.GuildId = (entry.GuildId ?? "").Trim();
        entry.DriverId = (entry.DriverId ?? "").Trim();
        entry.DriverName = (entry.DriverName ?? "").Trim();
        entry.AwardId = (entry.AwardId ?? "").Trim();
        entry.AwardedByUserId = (entry.AwardedByUserId ?? "").Trim();
        entry.AwardedByUsername = (entry.AwardedByUsername ?? "").Trim();
        entry.Note = (entry.Note ?? "").Trim();

        lock (_gate)
        {
            var exists = _items.Any(x =>
                string.Equals(x.GuildId, entry.GuildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DriverId, entry.DriverId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.AwardId, entry.AwardId, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                _items.Add(entry);
                Save();
            }

            return entry;
        }
    }

    public bool Remove(string guildId, string driverId, string awardId)
    {
        guildId = (guildId ?? "").Trim();
        driverId = (driverId ?? "").Trim();
        awardId = (awardId ?? "").Trim();

        lock (_gate)
        {
            var removed = _items.RemoveAll(x =>
                string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DriverId, driverId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.AwardId, awardId, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
                Save();

            return removed > 0;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _items = new List<DriverAwardEntry>();
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                _items = JsonSerializer.Deserialize<List<DriverAwardEntry>>(json, _readOptions) ?? new List<DriverAwardEntry>();
            }
            catch
            {
                _items = new List<DriverAwardEntry>();
            }
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_items, _writeOptions);
        File.WriteAllText(_path, json);
    }
}
