// BOT SIDE OPTIONAL PATCH
// Put inside Routes/ApiRoutes.cs RegisterCore(...) or wherever your /api routes are registered.
// This supports ELD fallback when no category webhook URL is saved locally.

public sealed class EldNotificationPushRequest
{
    public string GuildId { get; set; } = "";
    public string Category { get; set; } = "System";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Details { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string DefaultChannelName { get; set; } = "eld-notifications";
}

r.MapPost("/notifications/push", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<EldNotificationPushRequest>(jsonRead);
    if (req == null) return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(req.GuildId)) return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

    if (!ulong.TryParse(req.GuildId.Trim(), out var gid))
        return Results.Json(new { ok = false, error = "BadGuildId" }, statusCode: 400);

    var guild = services.Client?.GetGuild(gid);
    if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

    SocketTextChannel? channel = null;

    if (ulong.TryParse((req.ChannelId ?? "").Trim(), out var cid))
        channel = guild.GetTextChannel(cid);

    if (channel == null)
    {
        var desired = NormalizeNotifyChannel(req.DefaultChannelName);
        channel = guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name) == desired);
    }

    channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name) == "eld-notifications");
    channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name) == "announcements");
    channel ??= guild.TextChannels.FirstOrDefault(c => NormalizeNotifyChannel(c.Name).Contains("system"));

    if (channel == null) return Results.Json(new { ok = false, error = "NotificationChannelNotFound" }, statusCode: 404);

    var embed = new EmbedBuilder()
        .WithTitle($"{NotifyIcon(req.Category)} {FirstNonBlank(req.Title, "OverWatch ELD Notification")}")
        .WithDescription(FirstNonBlank(req.Message, "Notification") + (string.IsNullOrWhiteSpace(req.Details) ? "" : "\n\n" + req.Details))
        .WithColor(NotifyColor(req.Category))
        .WithFooter($"OverWatch ELD • {FirstNonBlank(req.Category, "System")}")
        .WithCurrentTimestamp()
        .Build();

    await channel.SendMessageAsync(embed: embed);

    return Results.Json(new { ok = true, channelId = channel.Id.ToString(), channelName = channel.Name }, jsonWrite);
});

static string NormalizeNotifyChannel(string? value)
{
    return (value ?? "").Trim().TrimStart('#').ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
}

static string NotifyIcon(string? category)
{
    return (category ?? "").Trim().ToLowerInvariant() switch
    {
        "dispatch" => "📦",
        "events" => "📅",
        "convoys" => "🚚",
        "garages" => "🏢",
        "fleet" => "🚛",
        "maintenance" => "🛠️",
        "achievements" => "🏆",
        "endorsements" => "🪪",
        _ => "📣"
    };
}

static Color NotifyColor(string? category)
{
    return (category ?? "").Trim().ToLowerInvariant() switch
    {
        "dispatch" => Color.Blue,
        "events" => Color.Purple,
        "convoys" => Color.Orange,
        "garages" => Color.Green,
        "fleet" => Color.Teal,
        "maintenance" => Color.Red,
        "achievements" => Color.Gold,
        "endorsements" => Color.LightGrey,
        _ => Color.DarkGrey
    };
}
