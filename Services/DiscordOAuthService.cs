using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Services;

public sealed class DiscordOAuthService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DiscordOAuthOptions _options;

    public DiscordOAuthService(HttpClient http, IOptions<DiscordOAuthOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string BuildAuthorizeUrl(string state)
    {
        var values = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scopes,
            ["state"] = state,
            ["prompt"] = "none"
        };

        var query = string.Join("&", values.Select(x =>
            $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value ?? "")}"));

        return $"https://discord.com/oauth2/authorize?{query}";
    }

    public async Task<DiscordTokenResponse?> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri
        });

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return null;

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<DiscordTokenResponse>(stream, JsonOpts, ct);
    }

    public async Task<DiscordUserDto?> GetCurrentUserAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return null;

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<DiscordUserDto>(stream, JsonOpts, ct);
    }

    public async Task<List<DiscordGuildDto>> GetCurrentUserGuildsAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me/guilds");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return new List<DiscordGuildDto>();

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<List<DiscordGuildDto>>(stream, JsonOpts, ct)
               ?? new List<DiscordGuildDto>();
    }
}
