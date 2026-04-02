using System.Net.Http.Headers;
using System.Text.Json;

public class DiscordOAuthService
{
    private readonly HttpClient _http = new();

    public async Task<string> ExchangeCodeAsync(string code, string clientId, string clientSecret, string redirectUri)
    {
        var dict = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        var res = await _http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(dict));
        var json = await res.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    public async Task<JsonElement> GetUserAsync(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _http.GetStringAsync("https://discord.com/api/users/@me");
        return JsonDocument.Parse(res).RootElement.Clone();
    }

    public async Task<JsonElement> GetGuildsAsync(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _http.GetStringAsync("https://discord.com/api/users/@me/guilds");
        return JsonDocument.Parse(res).RootElement.Clone();
    }
}
