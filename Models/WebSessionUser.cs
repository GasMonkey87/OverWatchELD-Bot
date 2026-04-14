namespace OverWatchELD.VtcBot.Models;

public sealed class WebSessionUser
{
    public string DiscordUserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? GlobalName { get; set; }
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
}
