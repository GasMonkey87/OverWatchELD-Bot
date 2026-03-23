using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores;

public sealed class DriverPerformance
{
    public string DiscordUserId { get; set; } = "";
    public double MilesWeek { get; set; }
    public double MilesMonth { get; set; }
    public double MilesTotal { get; set; }
    public int LoadsWeek { get; set; }
    public int LoadsMonth { get; set; }
    public int LoadsTotal { get; set; }
    public double PerformancePct { get; set; }
    public double Score { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Source { get; set; }
}

public static class PerformanceScoring
{
    public static double ComputeScore(DriverPerformance p)
    {
        var miles = p.MilesWeek * 1.0;
        var loads = p.LoadsWeek * 250.0;
        var pct = p.PerformancePct * 500.0;
        return miles + loads + pct;
    }
}

public sealed class PerformanceStore
{
    private readonly string _dir;
    private readonly JsonSerializerOptions _jsonRead;
    private readonly JsonSerializerOptions _jsonWrite;
    private readonly object _lock = new();

    public PerformanceStore(string dir, JsonSerializerOptions jsonRead, JsonSerializerOptions jsonWrite)
    {
        _dir = dir;
        _jsonRead = jsonRead;
        _jsonWrite = jsonWrite;
        Directory.CreateDirectory(_dir);
    }

    private string PathForGuild(string guildId)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();
        return Path.Combine(_dir, $"performance_{guildId}.json");
    }

    public Dictionary<string, DriverPerformance> Load(string guildId)
    {
        var path = PathForGuild(guildId);
        lock (_lock)
        {
            try
            {
                if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, DriverPerformance>>(json, _jsonRead);
                return dict != null
                    ? new Dictionary<string, DriverPerformance>(dict, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, DriverPerformance>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Save(string guildId, Dictionary<string, DriverPerformance> dict)
    {
        var path = PathForGuild(guildId);
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _dir);
                File.WriteAllText(path, JsonSerializer.Serialize(dict, _jsonWrite));
            }
            catch { }
        }
    }

    public void Upsert(string guildId, DriverPerformance perf)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();

        var uid = (perf?.DiscordUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(uid)) return;

        var dict = Load(guildId);
        perf.DiscordUserId = uid;
        perf.UpdatedUtc = DateTimeOffset.UtcNow;
        perf.Score = PerformanceScoring.ComputeScore(perf);
        dict[uid] = perf;
        Save(guildId, dict);
    }

    public (DriverPerformance? perf, int rank, int total) GetWithRank(string guildId, string discordUserId)
    {
        var dict = Load(guildId);
        if (!dict.TryGetValue((discordUserId ?? "").Trim(), out var me))
            return (null, 0, dict.Count);

        var list = dict.Values
            .Select(p => { p.Score = PerformanceScoring.ComputeScore(p); return p; })
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.PerformancePct)
            .ThenByDescending(p => p.MilesWeek)
            .ThenByDescending(p => p.LoadsWeek)
            .ToList();

        var idx = list.FindIndex(x => string.Equals(x.DiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase));
        return (me, idx >= 0 ? idx + 1 : 0, list.Count);
    }

    public List<DriverPerformance> GetTop(string guildId, int take)
    {
        take = Math.Clamp(take, 1, 50);

        return Load(guildId).Values
            .Select(p => { p.Score = PerformanceScoring.ComputeScore(p); return p; })
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.PerformancePct)
            .ThenByDescending(p => p.MilesWeek)
            .ThenByDescending(p => p.LoadsWeek)
            .Take(take)
            .ToList();
    }
}
