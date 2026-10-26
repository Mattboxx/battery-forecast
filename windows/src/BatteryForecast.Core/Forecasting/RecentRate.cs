using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Forecasting;

public enum RecentMethod { Power, Slope }

/// <summary>Stima dal consumo recente: "se continui così". Non conosce le abitudini, solo l'ultimo tratto.</summary>
public sealed record RecentEstimate(RecentMethod Method, double PctPerHour, DateTimeOffset? ReserveAt, TimeSpan Window);

/// <summary>
/// Stima leggera senza PyTorch, usata al primo avvio, quando il motore non c'è e come confronto.
/// Con potenza ed energia note: energia sopra la riserva / mediana della potenza recente.
/// Altrimenti: pendenza robusta (Theil–Sen) della percentuale sull'ultimo tratto continuo a batteria.
/// </summary>
public static class RecentRate
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    static readonly TimeSpan PowerWindow = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MinSlopeSpan = TimeSpan.FromMinutes(15);
    const double MinDropPct = 2;          // una differenza dell'1% è rumore di quantizzazione
    const int MinPowerSamples = 3;
    const double MinPctPerHour = 0.2;     // sotto, la batteria è praticamente ferma: nessuna stima

    /// <param name="samples">Campioni in ordine cronologico; l'ultimo è quello attuale.</param>
    public static RecentEstimate? Estimate(IReadOnlyList<BatterySample> samples, double reservePct)
    {
        var tail = CurrentDischargeTail(samples);
        if (tail.Count == 0) return null;
        var now = tail[^1];
        if (now.LevelPct is not double level || level <= reservePct) return null;
        return ByPower(tail, now, level, reservePct) ?? BySlope(tail, now, level, reservePct);
    }

    /// <summary>Ultimo tratto continuo a batteria: si ferma a un alimentatore, a una lacuna o a una risalita.</summary>
    internal static List<BatterySample> CurrentDischargeTail(IReadOnlyList<BatterySample> samples)
    {
        // The immediate reading wins over a stored sample with the same second.
        samples = samples.GroupBy(s => s.TimestampUtc).Select(g => g.Last()).OrderBy(s => s.TimestampUtc).ToArray();
        var tail = new List<BatterySample>();
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            var s = samples[i];
            if (!s.IsDischarging) break;
            if (samples[^1].DesignMWh is int currentDesign && s.DesignMWh is int design && design != currentDesign) break;
            if (i > 0 && i < samples.Count - 1 && IsSpike(samples[i - 1], s, samples[i + 1])) continue;
            if (tail.Count > 0)
            {
                var next = tail[^1];
                if (next.TimestampUtc - s.TimestampUtc > MaxSampleGap) break;          // sospensione
                if (s.LevelPct < next.LevelPct - RiseTolerance) break;                   // risalita: ricarica non vista
                if (tail[0].TimestampUtc - s.TimestampUtc > Window) break;
            }
            tail.Add(s);
        }
        tail.Reverse();
        return tail;
    }

    const double RiseTolerance = 1.5, SpikePct = 3;

    /// <summary>Lettura isolata lontana da entrambi i vicini nella stessa direzione: salto del sensore.</summary>
    static bool IsSpike(BatterySample prev, BatterySample s, BatterySample next) =>
        prev.LevelPct is double a && s.LevelPct is double b && next.LevelPct is double c &&
        Math.Abs(b - a) > SpikePct && Math.Abs(b - c) > SpikePct && Math.Sign(b - a) == Math.Sign(b - c);

    static RecentEstimate? ByPower(List<BatterySample> tail, BatterySample now, double level, double reserve)
    {
        if (now.RemainingMWh is not int remaining || now.FullChargeMWh is not int full) return null;
        var powers = tail.Where(s => now.TimestampUtc - s.TimestampUtc <= PowerWindow && s.DischargePowerMW is > 0)
                         .Select(s => (double)s.DischargePowerMW!.Value).Order().ToArray();
        if (powers.Length < MinPowerSamples) return null;
        var mw = powers[powers.Length / 2];
        var usable = remaining - reserve / 100 * full;
        if (usable <= 0 || mw <= 0) return null;
        var pctPerHour = mw / full * 100;
        if (pctPerHour < MinPctPerHour) return null;
        var hours = usable / mw;
        return new RecentEstimate(RecentMethod.Power, pctPerHour, now.TimestampUtc.AddHours(hours),
            now.TimestampUtc - tail[0].TimestampUtc);
    }

    static RecentEstimate? BySlope(List<BatterySample> tail, BatterySample now, double level, double reserve)
    {
        var pts = tail.Select(s => ((s.TimestampUtc - tail[0].TimestampUtc).TotalHours, s.LevelPct!.Value)).ToArray();
        var span = now.TimestampUtc - tail[0].TimestampUtc;
        if (span < MinSlopeSpan || pts[0].Item2 - level < MinDropPct) return null;
        var slopes = new List<double>();
        for (var i = 0; i < pts.Length; i++)
            for (var j = i + 1; j < pts.Length; j++)
                if (pts[j].Item1 > pts[i].Item1)
                    slopes.Add((pts[j].Item2 - pts[i].Item2) / (pts[j].Item1 - pts[i].Item1));
        if (slopes.Count == 0) return null;
        slopes.Sort();
        var perHour = -slopes[slopes.Count / 2];
        if (perHour < MinPctPerHour) return null;
        return new RecentEstimate(RecentMethod.Slope, perHour, now.TimestampUtc.AddHours((level - reserve) / perHour), span);
    }
}
