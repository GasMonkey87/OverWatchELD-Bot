namespace OverWatchELD.VtcBot.Models;

public sealed class WebSessionUser
{
    // Email/password account fields
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsEmailAccount { get; set; }

    // Discord fields. These are optional for email accounts until they link Discord.
    public string DiscordUserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? GlobalName { get; set; }
    public string AccessToken { get; set; } = "";

    public DateTimeOffset ExpiresUtc { get; set; }
}
