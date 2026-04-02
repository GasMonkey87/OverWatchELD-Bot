namespace OverWatchELD.VtcBot.Models;

public sealed class DriverAwardEntry
{
    public string GuildId { get; set; } = "";
    public string DriverId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string AwardId { get; set; } = "";
    public string AwardedByUserId { get; set; } = "";
    public string AwardedByUsername { get; set; } = "";
    public DateTime AwardedUtc { get; set; } = DateTime.UtcNow;
    public string Note { get; set; } = "";
}
