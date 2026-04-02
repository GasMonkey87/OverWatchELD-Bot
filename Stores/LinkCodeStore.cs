using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores;

public sealed class LinkCodeEntry
{
    public string Code { get; set; } = "";
    public string GuildId { get; set; } = "0";
    public string GuildName { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DiscordUsername { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(30);
}

public sealed class LinkCodeStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonRead;
    private readonly JsonSerializerOptions _jsonWrite;
    private Dictionary<string, LinkCodeEntry> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public LinkCodeStore(string path, JsonSerializerOptions jsonRead, JsonSerializerOptions jsonWrite)
    {
        _path = path;
        _jsonRead = jsonRead;
        _jsonWrite = jsonWrite;
        Load();
    }

    public void Put(LinkCodeEntry entry)
    {
        if (entry == null) throw new InvalidOperationException("Entry required.");

        var code = (entry.Code ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Code required.");

        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            entry.Code = code;
            if (entry.CreatedUtc == default) entry.CreatedUtc = now;
            if (entry.ExpiresUtc <= now) entry.ExpiresUtc = now.AddMinutes(30);

            _byCode[code] = entry;
            Prune_NoLock(now);
            Save_NoLock();
        }
    }

    public bool Consume(string code, out LinkCodeEntry entry)
    {
        entry = new LinkCodeEntry();
        code = (code ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code)) return false;

        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            Prune_NoLock(now);

            if (!_byCode.TryGetValue(code, out var e)) return false;
            if (e.ExpiresUtc <= now)
            {
                _byCode.Remove(code);
                Save_NoLock();
                return false;
            }

            _byCode.Remove(code);
            Save_NoLock();
            entry = Clone(e);
            return true;
        }
    }

    private void Prune_NoLock(DateTimeOffset now)
    {
        var expired = _byCode.Where(kvp => kvp.Value.ExpiresUtc <= now).Select(kvp => kvp.Key).ToList();
        foreach (var k in expired) _byCode.Remove(k);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _byCode = new(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var json = File.ReadAllText(_path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, LinkCodeEntry>>(json, _jsonRead)
                       ?? new Dictionary<string, LinkCodeEntry>();

            _byCode = new Dictionary<string, LinkCodeEntry>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _byCode = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save_NoLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(_byCode, _jsonWrite));
        }
        catch { }
    }

    private static LinkCodeEntry Clone(LinkCodeEntry e) => new()
    {
        Code = e.Code,
        GuildId = e.GuildId,
        GuildName = e.GuildName,
        DiscordUserId = e.DiscordUserId,
        DiscordUsername = e.DiscordUsername,
        CreatedUtc = e.CreatedUtc,
        ExpiresUtc = e.ExpiresUtc
    };
}

public sealed class LinkedDriverEntry
{
    public string GuildId { get; set; } = "0";
    public string DiscordUserId { get; set; } = "";
    public string DiscordUserName { get; set; } = "";
    public DateTimeOffset LinkedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LastCode { get; set; }
}

public sealed class LinkedDriversStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonRead;
    private readonly JsonSerializerOptions _jsonWrite;
    private Dictionary<string, Dictionary<string, LinkedDriverEntry>> _byGuild =
        new(StringComparer.OrdinalIgnoreCase);

    public LinkedDriversStore(string path, JsonSerializerOptions jsonRead, JsonSerializerOptions jsonWrite)
    {
        _path = path;
        _jsonRead = jsonRead;
        _jsonWrite = jsonWrite;
        Load();
    }

    public void Link(string guildId, string discordUserId, string discordUserName, string? code)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();
        discordUserId = (discordUserId ?? "").Trim();
        discordUserName = (discordUserName ?? "").Trim();
        code = (code ?? "").Trim();

        if (string.IsNullOrWhiteSpace(discordUserId)) return;

        lock (_lock)
        {
            if (!_byGuild.TryGetValue(guildId, out var byUser))
            {
                byUser = new Dictionary<string, LinkedDriverEntry>(StringComparer.OrdinalIgnoreCase);
                _byGuild[guildId] = byUser;
            }

            byUser[discordUserId] = new LinkedDriverEntry
            {
                GuildId = guildId,
                DiscordUserId = discordUserId,
                DiscordUserName = discordUserName,
                LinkedUtc = DateTimeOffset.UtcNow,
                LastCode = string.IsNullOrWhiteSpace(code) ? null : code
            };

            Save_NoLock();
        }
    }

    public List<LinkedDriverEntry> List(string guildId)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();

        lock (_lock)
        {
            if (!_byGuild.TryGetValue(guildId, out var byUser) || byUser == null)
                return new List<LinkedDriverEntry>();

            return byUser.Values
                .Select(x => new LinkedDriverEntry
                {
                    GuildId = x.GuildId,
                    DiscordUserId = x.DiscordUserId,
                    DiscordUserName = x.DiscordUserName,
                    LinkedUtc = x.LinkedUtc,
                    LastCode = x.LastCode
                })
                .OrderByDescending(x => x.LinkedUtc)
                .ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _byGuild = new(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var json = File.ReadAllText(_path);
            _byGuild = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, LinkedDriverEntry>>>(json, _jsonRead)
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _byGuild = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save_NoLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, _jsonWrite));
        }
        catch { }
    }
}
