using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Forecasting;

public enum ChargeEstimateKind { Collecting, AlreadyReached, RecentRate, PastSessions }
public sealed record ChargeEstimate(ChargeEstimateKind Kind, int TargetPct, DateTimeOffset? TargetAt = null, int Sessions = 0);

/// <summary>Observed charging only. Never calls the discharge model or promises constant speed near full.</summary>
public static class ChargeRate
{
    public static ChargeEstimate Estimate(IReadOnlyList<BatterySample> history, BatterySample current, int target)
    {
        var pending = new ChargeEstimate(ChargeEstimateKind.Collecting, target);
        if (current.OnAc != true || current.State != ChargeState.Charging || current.LevelPct is not double level ||
            !double.IsFinite(level) || level is < 0 or > 100 || target is < 50 or > 100) return pending;
        if (level >= target) return new(ChargeEstimateKind.AlreadyReached, target);
        var points = history.Append(current).DistinctBy(s => s.TimestampUtc).OrderBy(s => s.TimestampUtc).ToArray();
        var tail = new List<BatterySample>();
        foreach (var s in points.Reverse())
        {
            if (!Valid(s, current) || current.TimestampUtc - s.TimestampUtc > TimeSpan.FromMinutes(30)) break;
            if (tail.Count > 0 && !Continuous(s, tail[^1])) break;
            tail.Add(s);
        }
        tail.Reverse();
        if (tail.Count < 6 || current.TimestampUtc - tail[0].TimestampUtc < TimeSpan.FromMinutes(8) ||
            level - tail[0].LevelPct < 2) return pending;
        var slopes = new List<double>();
        for (int i = 0; i < tail.Count; i++)
            for (int j = i + 1; j < tail.Count; j++)
            {
                var minutes = (tail[j].TimestampUtc - tail[i].TimestampUtc).TotalMinutes;
                if (minutes >= 3) slopes.Add((tail[j].LevelPct!.Value - tail[i].LevelPct!.Value) / minutes);
            }
        slopes.Sort();
        if (slopes.Count == 0 || slopes[slopes.Count / 2] < 0.02) return pending;

        // Match complete observed level intervals in previous charging sessions. This includes taper.
        var durations = new List<double>();
        List<BatterySample> segment = [];
        void Flush()
        {
            if (segment.Count >= 3)
            {
                DateTimeOffset? start = Crossing(segment, level), end = Crossing(segment, target);
                if (start is { } a && end is { } b && b > a) durations.Add((b-a).TotalMinutes);
            }
            segment.Clear();
        }
        foreach (var s in points)
        {
            if (!Valid(s, current)) { Flush(); continue; }
            if (segment.Count > 0 && !Continuous(segment[^1], s)) Flush();
            segment.Add(s);
        }
        Flush();
        durations.Sort();
        if (durations.Count >= 2)
            return new(ChargeEstimateKind.PastSessions, target, current.TimestampUtc.AddMinutes(durations[durations.Count / 2]), durations.Count);
        // A short local projection is allowed only below the taper region and within 20 points.
        if (target > 80 || target - level > 20) return pending;
        var projected = (target - level) / slopes[slopes.Count / 2];
        if (projected > 240) return pending;
        return new(ChargeEstimateKind.RecentRate, target, current.TimestampUtc.AddMinutes(projected));
    }

    static bool Valid(BatterySample s, BatterySample current) => s.OnAc == true && s.State == ChargeState.Charging &&
        s.LevelPct is >= 0 and <= 100 && s.DesignMWh == current.DesignMWh &&
        (s.FullChargeMWh is null || current.FullChargeMWh is null ||
            Math.Abs(s.FullChargeMWh.Value - current.FullChargeMWh.Value) <= current.FullChargeMWh.Value * 0.1);

    static bool Continuous(BatterySample a, BatterySample b) => b.TimestampUtc > a.TimestampUtc &&
        b.TimestampUtc - a.TimestampUtc <= TimeSpan.FromMinutes(3) && b.LevelPct >= a.LevelPct - 0.5 &&
        b.LevelPct - a.LevelPct <= 5;

    static DateTimeOffset? Crossing(List<BatterySample> points, double level)
    {
        if (points[0].LevelPct > level) return null;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i-1]; var b = points[i];
            if (a.LevelPct <= level && b.LevelPct >= level && b.LevelPct > a.LevelPct)
                return a.TimestampUtc + TimeSpan.FromTicks((long)((b.TimestampUtc-a.TimestampUtc).Ticks *
                    (level-a.LevelPct!.Value)/(b.LevelPct!.Value-a.LevelPct.Value)));
        }
        return null;
    }
}
