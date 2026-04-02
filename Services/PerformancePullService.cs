using System.Text.Json;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Services;

public static class PerformancePullService
{
    public static async Task RunAsync(DiscordSocketClient? client, Func<bool> isReady, PerformanceStore? perfStore, HttpClient http, JsonSerializerOptions jsonRead)
    {
        var baseUrl = (Environment.GetEnvironmentVariable("ELD_BASE_URL") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || !baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;

        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2));

                if (client == null || !isReady() || perfStore == null)
                    continue;

                foreach (var g in client.Guilds)
                {
                    try
                    {
                        var url = $"{baseUrl.TrimEnd('/')}/api/performance?guildId={g.Id}";
                        using var resp = await http.GetAsync(url);
                        if (!resp.IsSuccessStatusCode) continue;

                        var json = await resp.Content.ReadAsStringAsync();
                        if (string.IsNullOrWhiteSpace(json)) continue;

                        List<DriverPerformance>? drivers = null;

                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("drivers", out var dEl) &&
                                dEl.ValueKind == JsonValueKind.Array)
                            {
                                drivers = JsonSerializer.Deserialize<List<DriverPerformance>>(dEl.GetRawText(), jsonRead);
                            }
                        }
                        catch { }

                        if (drivers == null)
                        {
                            try
                            {
                                if (json.TrimStart().StartsWith("["))
                                    drivers = JsonSerializer.Deserialize<List<DriverPerformance>>(json, jsonRead);
                            }
                            catch { }
                        }

                        if (drivers == null || drivers.Count == 0) continue;

                        foreach (var p in drivers)
                        {
                            if (p == null) continue;
                            p.DiscordUserId = (p.DiscordUserId ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(p.DiscordUserId)) continue;
                            p.PerformancePct = Math.Clamp(p.PerformancePct, 0, 100);
                            p.Score = PerformanceScoring.ComputeScore(p);
                            p.Source = "eld-pull";
                            perfStore.Upsert(g.Id.ToString(), p);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
