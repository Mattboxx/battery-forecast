using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Presentation;

/// <summary>Observed discharge segments, not complete cycles or time-to-empty measurements.</summary>
public sealed record DischargeSegment(DateTimeOffset Start, DateTimeOffset End, double StartPct,
    double EndPct, double? EnergyWh, int Samples)
{
    public TimeSpan Duration => End - Start;
}

public static class HistorySummary
{
    public static List<DischargeSegment> Segments(IReadOnlyList<BatterySample> samples,
        IReadOnlyList<DateTimeOffset> interruptions)
    {
        var result = new List<DischargeSegment>();
        BatterySample? first = null, last = null;
        int count = 0, eventIndex = 0;
        bool energyValid = true;
        void Flush()
        {
            if (first is not null && last is not null && count >= 2 && first.LevelPct >= last.LevelPct)
                result.Add(new(first.TimestampUtc, last.TimestampUtc, first.LevelPct!.Value, last.LevelPct!.Value,
                    energyValid && first.RemainingMWh is int a && last.RemainingMWh is int b && a >= b ? (a-b)/1000.0 : null, count));
            first = last = null; count = 0; energyValid = true;
        }
        foreach (var s in samples)
        {
            bool interrupted = false;
            while (eventIndex < interruptions.Count && interruptions[eventIndex] <= s.TimestampUtc)
            {
                if (last is not null && interruptions[eventIndex] > last.TimestampUtc) interrupted = true;
                eventIndex++;
            }
            if (!s.IsDischarging || s.State == ChargeState.Unknown || s.LevelPct is < 0 or > 100)
            { Flush(); continue; }
            if (last is not null && (interrupted || s.TimestampUtc - last.TimestampUtc > TimeSpan.FromMinutes(5) ||
                s.TimestampUtc <= last.TimestampUtc || s.DesignMWh != last.DesignMWh || s.LevelPct > last.LevelPct + 1.5)) Flush();
            if (last is not null && (s.RemainingMWh > last.RemainingMWh || s.FullChargeMWh != last.FullChargeMWh)) energyValid = false;
            if (s.RemainingMWh is null) energyValid = false;
            first ??= s; last = s; count++;
        }
        Flush();
        result.Reverse();
        return result;
    }
}
