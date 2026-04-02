using System.Text.Json.Serialization;

namespace OverWatchELD.VtcBot.Models;

public sealed class SendMessageReq
{
    public string? GuildId { get; set; }

    [JsonPropertyName("driverName")]
    public string? DisplayName { get; set; }

    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? DiscordUserId { get; set; }
    public string? DiscordUsername { get; set; }

    public string? Recipient { get; set; }
    public string? To { get; set; }
    public string? DriverDiscordUserId { get; set; }
    public string? RecipientDiscordUserId { get; set; }
    public string? ToDiscordUserId { get; set; }
    public string? DriverName { get; set; }
    public string? RecipientDiscordUsername { get; set; }
    public string? DriverDiscordUsername { get; set; }

    public string? LoadNumber { get; set; }
    public string? LoadNo { get; set; }
    public string? CurrentLoadNumber { get; set; }
    public string? TruckId { get; set; }
    public string? TruckNumber { get; set; }
    public string? AssignedTruck { get; set; }
    public string? AssignedTruckId { get; set; }

    public string Text { get; set; } = "";
    public string? Source { get; set; }
}

public sealed class MarkBulkReq
{
    public string? ChannelId { get; set; }
    public List<string>? MessageIds { get; set; }
}

public sealed class DeleteBulkReq
{
    public string? ChannelId { get; set; }
    public List<string>? MessageIds { get; set; }
}

public sealed class AnnouncementPostReq
{
    public string? GuildId { get; set; }
    public string? Text { get; set; }
    public string? Author { get; set; }
}

public sealed class RosterUpsertReq
{
    public string? DriverId { get; set; }
    public string? Name { get; set; }
    public string? DiscordUserId { get; set; }
    public string? DiscordUsername { get; set; }
    public string? TruckNumber { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
}
