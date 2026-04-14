using System.Text.Json.Serialization;

namespace OverWatchELD.VtcBot.Models;

public sealed class DiscordGuildDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("owner")]
    public bool Owner { get; set; }

    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }
}
