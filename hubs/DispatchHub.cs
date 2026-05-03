using Microsoft.AspNetCore.SignalR;

namespace OverWatchELD.VtcBot.Hubs;

public sealed class DispatchHub : Hub
{
    public async Task JoinGuild(string guildId)
    {
        guildId = (guildId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(guildId))
            await Groups.AddToGroupAsync(Context.ConnectionId, guildId);
    }

    public async Task LeaveGuild(string guildId)
    {
        guildId = (guildId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(guildId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, guildId);
    }
}
