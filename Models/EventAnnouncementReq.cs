namespace OverWatchELD.VtcBot.Models.Events;

public sealed class EventAnnouncementReq
{
    public string? GuildId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? StartLocal { get; set; }
    public string? EndLocal { get; set; }
    public string? CreatedBy { get; set; }
    public string? MentionText { get; set; }
}
