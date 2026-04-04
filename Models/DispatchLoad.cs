using System;

namespace OverWatchELD.VtcBot.Models;

public sealed class DispatchLoad
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GuildId { get; set; } = "";
    public string LoadNumber { get; set; } = "";
    public string Status { get; set; } = "unassigned";
    public string Priority { get; set; } = "normal";
    public string Commodity { get; set; } = "";
    public string PickupLocation { get; set; } = "";
    public string DropoffLocation { get; set; } = "";
    public string DriverDiscordUserId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string TruckId { get; set; } = "";
    public string DispatcherNotes { get; set; } = "";
    public string BolNumber { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AssignedUtc { get; set; }
    public DateTimeOffset? PickupUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? DueUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
