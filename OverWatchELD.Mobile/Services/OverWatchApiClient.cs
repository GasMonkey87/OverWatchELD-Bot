namespace OverWatchELD.Mobile.Services;

public sealed class OverWatchApiClient
{
    private readonly HttpClient _http = new();

    public string BaseUrl { get; set; } = "https://overwatcheld.up.railway.app";

    public async Task<bool> PingAsync()
    {
        try
        {
            var result = await _http.GetAsync(BaseUrl + "/api/ping");
            return result.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetDashboardAsync()
    {
        try
        {
            return await _http.GetStringAsync(BaseUrl + "/api/dashboard/summary");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
