using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OverWatchELD.VtcBot.Performance;

public sealed class PerformanceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _baseDir;

    public PerformanceStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    private string PathForGuild(string guildId) => System.IO.Path.Combine(_baseDir, $"performance_{guildId}.json");

    public Dictionary<string, DriverPerformance> Load(string guildId)
    {
        var path = PathForGuild(guildId);
        if (!File.Exists(path)) return new Dictionary<string, DriverPerformance>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, DriverPerformance>>(json, JsonOpts)
                   ?? new Dictionary<string, DriverPerformance>();
        }
        catch
        {
            return new Dictionary<string, DriverPerformance>();
        }
    }

    public void Save(string guildId, Dictionary<string, DriverPerformance> data)
    {
        var path = PathForGuild(guildId);
        var json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(path, json);
    }

    public (int rank, int total) GetRank(string guildId, string discordUserId)
    {
        var data = Load(guildId);

        var list = data.Values
            .Select(p =>
            {
                p.Score = PerformanceScoring.ComputeScore(p);
                return p;
            })
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.PerformancePct)
            .ThenByDescending(p => p.MilesWeek)
            .ThenByDescending(p => p.LoadsWeek)
            .ToList();

        var idx = list.FindIndex(x => x.DiscordUserId == discordUserId);
        return (idx >= 0 ? idx + 1 : 0, list.Count);
    }

    public List<DriverPerformance> GetTop(string guildId, int take)
    {
        var data = Load(guildId);
        return data.Values
            .Select(p =>
            {
                p.Score = PerformanceScoring.ComputeScore(p);
                return p;
            })
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.PerformancePct)
            .ThenByDescending(p => p.MilesWeek)
            .ThenByDescending(p => p.LoadsWeek)
            .Take(take)
            .ToList();
    }

    public void Upsert(string guildId, DriverPerformance perf)
    {
        var data = Load(guildId);

        perf.UpdatedUtc = DateTime.UtcNow;
        perf.Score = PerformanceScoring.ComputeScore(perf);

        data[perf.DiscordUserId] = perf;

        Save(guildId, data);
    }
}
