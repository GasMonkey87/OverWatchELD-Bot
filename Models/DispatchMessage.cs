using System;

namespace OverWatchELD.VtcBot.Models;

public sealed class DispatchMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GuildId { get; set; } = "";
    public string DriverDiscordUserId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string Direction { get; set; } = "to_driver";
    public string Text { get; set; } = "";
    public string LoadNumber { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
