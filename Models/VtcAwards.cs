namespace OverWatchELD.VtcBot.Models;

public sealed class VtcAward
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GuildId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconEmoji { get; set; } = "🏆";
    public string CreatedByUserId { get; set; } = "";
    public string CreatedByUsername { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsAchievement { get; set; }
}
