using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores;

public sealed class DispatchSettings
{
    public string GuildId { get; set; } = "";
    public string? DispatchChannelId { get; set; }
    public string? DispatchWebhookUrl { get; set; }
    public string? AnnouncementChannelId { get; set; }
    public string? AnnouncementWebhookUrl { get; set; }
}

public sealed class DispatchSettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonRead;
    private readonly JsonSerializerOptions _jsonWrite;
    private Dictionary<string, DispatchSettings> _byGuild = new();

    public DispatchSettingsStore(string path, JsonSerializerOptions jsonRead, JsonSerializerOptions jsonWrite)
    {
        _path = path;
        _jsonRead = jsonRead;
        _jsonWrite = jsonWrite;
        Load();
    }

    public DispatchSettings Get(string guildId)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();

        lock (_lock)
        {
            if (!_byGuild.TryGetValue(guildId, out var s))
            {
                s = new DispatchSettings { GuildId = guildId };
                _byGuild[guildId] = s;
                Save();
            }
            return s;
        }
    }

    public void SetDispatchChannel(string guildId, ulong channelId)
    {
        lock (_lock)
        {
            var s = Get(guildId);
            s.DispatchChannelId = channelId.ToString();
            Save();
        }
    }

    public void SetDispatchWebhook(string guildId, string url)
    {
        lock (_lock)
        {
            var s = Get(guildId);
            s.DispatchWebhookUrl = (url ?? "").Trim();
            Save();
        }
    }

    public void SetAnnouncementChannel(string guildId, ulong channelId)
    {
        lock (_lock)
        {
            var s = Get(guildId);
            s.AnnouncementChannelId = channelId.ToString();
            Save();
        }
    }

    public void SetAnnouncementWebhook(string guildId, string url)
    {
        lock (_lock)
        {
            var s = Get(guildId);
            s.AnnouncementWebhookUrl = (url ?? "").Trim();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) { _byGuild = new(); return; }
            var json = File.ReadAllText(_path);
            _byGuild = JsonSerializer.Deserialize<Dictionary<string, DispatchSettings>>(json, _jsonRead) ?? new();
        }
        catch
        {
            _byGuild = new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, _jsonWrite));
        }
        catch { }
    }
}
