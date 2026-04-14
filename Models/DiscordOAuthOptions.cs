namespace OverWatchELD.VtcBot.Models;

public sealed class DiscordOAuthOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scopes { get; set; } = "identify guilds";
}
