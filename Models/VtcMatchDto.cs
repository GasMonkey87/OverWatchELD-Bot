namespace OverWatchELD.VtcBot.Models;

public sealed class VtcMatchDto
{
    public string GuildId { get; set; } = "";
    public string VtcName { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string Role { get; set; } = "Driver";
    public bool IsManager { get; set; }
}
