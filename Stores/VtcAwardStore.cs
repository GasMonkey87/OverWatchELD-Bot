using System.Text.Json;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores;

public sealed class VtcAwardStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _readOptions;
    private readonly JsonSerializerOptions _writeOptions;
    private readonly object _gate = new();

    private List<VtcAward> _items = new();

    public VtcAwardStore(
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

    public List<VtcAward> GetAll(string guildId)
    {
        guildId = (guildId ?? "").Trim();

        lock (_gate)
        {
            return _items
                .Where(x => string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name)
                .ToList();
        }
    }

    public VtcAward? GetById(string guildId, string awardId)
    {
        guildId = (guildId ?? "").Trim();
        awardId = (awardId ?? "").Trim();

        lock (_gate)
        {
            return _items.FirstOrDefault(x =>
                string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Id, awardId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public VtcAward Add(VtcAward award)
    {
        if (award == null) throw new ArgumentNullException(nameof(award));

        award.GuildId = (award.GuildId ?? "").Trim();
        award.Name = (award.Name ?? "").Trim();
        award.Description = (award.Description ?? "").Trim();
        award.IconEmoji = string.IsNullOrWhiteSpace(award.IconEmoji) ? "🏆" : award.IconEmoji.Trim();
        award.CreatedByUserId = (award.CreatedByUserId ?? "").Trim();
        award.CreatedByUsername = (award.CreatedByUsername ?? "").Trim();

        if (string.IsNullOrWhiteSpace(award.Id))
            award.Id = Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            var existing = _items.FindIndex(x =>
                string.Equals(x.GuildId, award.GuildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Id, award.Id, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
                _items[existing] = award;
            else
                _items.Add(award);

            Save();
            return award;
        }
    }

    public bool Delete(string guildId, string awardId)
    {
        guildId = (guildId ?? "").Trim();
        awardId = (awardId ?? "").Trim();

        lock (_gate)
        {
            var removed = _items.RemoveAll(x =>
                string.Equals(x.GuildId, guildId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Id, awardId, StringComparison.OrdinalIgnoreCase));

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
                _items = new List<VtcAward>();
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                _items = JsonSerializer.Deserialize<List<VtcAward>>(json, _readOptions) ?? new List<VtcAward>();
            }
            catch
            {
                _items = new List<VtcAward>();
            }
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_items, _writeOptions);
        File.WriteAllText(_path, json);
    }
}
