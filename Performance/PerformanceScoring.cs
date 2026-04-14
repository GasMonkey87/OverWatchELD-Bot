using System;

namespace OverWatchELD.VtcBot.Performance;

public static class PerformanceScoring
{
    // Miles + Loads + Performance %
    // Tweak weights anytime; storage format stays same.
    public static double ComputeScore(DriverPerformance p)
    {
        var miles = p.MilesWeek * 1.0;
        var loads = p.LoadsWeek * 250.0;
        var pct   = p.PerformancePct * 500.0;
        return miles + loads + pct;
    }
}
