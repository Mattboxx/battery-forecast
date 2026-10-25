namespace BatteryForecast.Core.Forecasting;

public readonly record struct CurvePoint(DateTimeOffset T, double P10, double P50, double P90);

public enum CrossingStatus { AlreadyBelow, WithinHorizon, BeyondHorizon }
public enum HistoryQuality { Insufficient, Learning, Ok }

/// <summary>Previsione Timestral in forma comoda per la UI. Immutabile.</summary>
public sealed record ForecastView(
    DateTimeOffset Generated, double Level, double ReservePct, CrossingStatus Crossing,
    DateTimeOffset? Central, DateTimeOffset? Early, DateTimeOffset? Late, IReadOnlyList<CurvePoint> Curve,
    HistoryQuality Quality, double ObservedDays, int Sessions, string? ModelId, string? ModelRevision,
    DateTimeOffset? HabitualRecharge = null, DateTimeOffset? ExtrapolatedFrom = null)
{
    public DateTimeOffset HorizonEnd => Curve.Count > 0 ? Curve[^1].T : Generated;

    public static ForecastView From(ForecastResponse r) => new(
        FromUnix(r.ContextEndUtc), r.Level, r.ReservePct,
        r.Crossing.Status switch
        {
            "already_below" => CrossingStatus.AlreadyBelow,
            "within_horizon" => CrossingStatus.WithinHorizon,
            _ => CrossingStatus.BeyondHorizon,
        },
        Opt(r.Crossing.CentralUtc), Opt(r.Crossing.EarlyUtc), Opt(r.Crossing.LateUtc),
        [.. r.Curve.Select(p => new CurvePoint(FromUnix(p.T), p.P10, p.P50, p.P90))],
        r.Quality.State switch { "ok" => HistoryQuality.Ok, "learning" => HistoryQuality.Learning, _ => HistoryQuality.Insufficient },
        r.Quality.ObservedDays, r.Quality.DischargeSessions, r.ModelId, r.ModelRevision,
        Opt(r.HabitualRechargeUtc), Opt(r.ExtrapolatedFromUtc));

    /// <summary>Livello interpolato all'istante <paramref name="t"/>; null fuori dalla curva.</summary>
    public CurvePoint? At(DateTimeOffset t)
    {
        if (Curve.Count == 0 || t < Generated || t > HorizonEnd) return null;
        var prev = new CurvePoint(Generated, Level, Level, Level);
        foreach (var p in Curve)
        {
            if (p.T >= t)
            {
                var span = (p.T - prev.T).TotalSeconds;
                var k = span <= 0 ? 1 : (t - prev.T).TotalSeconds / span;
                return new CurvePoint(t, Lerp(prev.P10, p.P10, k), Lerp(prev.P50, p.P50, k), Lerp(prev.P90, p.P90, k));
            }
            prev = p;
        }
        return null;
    }

    static double Lerp(double a, double b, double k) => a + (b - a) * k;
    static DateTimeOffset FromUnix(double s) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(s * 1000));
    static DateTimeOffset? Opt(double? s) => s is double v ? FromUnix(v) : null;
}

/// <summary>Obiettivo orario: oggi all'ora scelta, oppure domani se è già passata (regola mostrata in UI).</summary>
public sealed record TargetTime(DateTimeOffset When, bool Tomorrow)
{
    public static TargetTime Next(DateTimeOffset now, TimeOnly time, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = local.Date + time.ToTimeSpan();
        var tomorrow = today <= local.DateTime;
        var wall = tomorrow ? today.AddDays(1) : today;
        // Ora legale: un orario che non esiste quel giorno si sposta in avanti di un'ora.
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return new TargetTime(new DateTimeOffset(wall, zone.GetUtcOffset(wall)), tomorrow);
    }
}
