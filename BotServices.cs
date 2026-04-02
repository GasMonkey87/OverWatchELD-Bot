using Discord.WebSocket;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Services;

public sealed class BotServices
{
    public DiscordSocketClient? Client { get; set; }
    public bool DiscordReady { get; set; }

    public OverWatchELD.VtcBot.Threads.ThreadMapStore? ThreadStore { get; set; }
    public DispatchSettingsStore? DispatchStore { get; set; }
    public VtcRosterStore? RosterStore { get; set; }
    public LinkCodeStore? LinkCodeStore { get; set; }
    public LinkedDriversStore? LinkedDriversStore { get; set; }
    public PerformanceStore? PerformanceStore { get; set; }
    public DriverStatusStore? DriverStatusStore { get; set; }
    public VtcAwardStore? AwardStore { get; set; }
    public DriverAwardStore? DriverAwardStore { get; set; }

    // Message de-dupe state used by BotCommandHandler
    public object HandledMessageLock { get; } = new();
    public Dictionary<ulong, DateTime> HandledMessages { get; } = new();
}
