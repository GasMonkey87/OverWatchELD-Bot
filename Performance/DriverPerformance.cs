using System;

namespace OverWatchELD.VtcBot.Performance;

public sealed class DriverPerformance
{
    public string DiscordUserId { get; set; } = "";
    public double MilesWeek { get; set; }
    public double MilesMonth { get; set; }
    public double MilesTotal { get; set; }

    public int LoadsWeek { get; set; }
    public int LoadsMonth { get; set; }
    public int LoadsTotal { get; set; }

    public double PerformancePct { get; set; } // 0..100 from ELD

    public double Score { get; set; }          // computed by bot
    public DateTime UpdatedUtc { get; set; }   // last update time
}
