using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OverWatchELD.Services;

public sealed class VtcTelemetryPosterService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private DateTimeOffset _lastPostUtc = DateTimeOffset.MinValue;

    public async Task PostAsync(
        string botApiBaseUrl,
        string guildId,
        string driverDiscordUserId,
        string driverName,
        string truckName,
        double x,
        double y,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botApiBaseUrl)) return;
        if (string.IsNullOrWhiteSpace(guildId)) return;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastPostUtc).TotalSeconds < 3)
            return;

        _lastPostUtc = now;

        var url = botApiBaseUrl.TrimEnd('/') + "/api/telemetry";

        var payload = new
        {
            guildId,
            driverDiscordUserId,
            driver = driverName,
            truck = truckName,
            x,
            y,
            status
        };

        var json = JsonSerializer.Serialize(payload);

        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Keep ELD stable if bot/API is offline.
        }
    }
}
