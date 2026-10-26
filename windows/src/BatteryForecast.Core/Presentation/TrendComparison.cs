using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Core.Presentation;

public sealed record TrendPoint(BatterySample Actual, CurvePoint Predicted, bool ScenarioChanged, bool Interrupted)
{
    public double Difference => Actual.LevelPct!.Value - Predicted.P50;
}

/// <summary>Individual observations against an already-issued curve; no aggregate accuracy scores.</summary>
public static class TrendComparison
{
    public static TrendPoint? At(ForecastSnapshot? snapshot, BatterySample actual,
        IReadOnlyList<BatterySample> history, IReadOnlyList<DateTimeOffset> breaks)
    {
        if (snapshot is null || actual.TimestampUtc < snapshot.IssuedAt || actual.LevelPct is not (>= 0 and <= 100) ||
            snapshot.View.At(actual.TimestampUtc) is not { } predicted) return null;
        var origin = snapshot.View.Generated;
        var observed = history.Where(s => s.TimestampUtc >= origin && s.TimestampUtc <= actual.TimestampUtc).ToArray();
        bool expectedAc = snapshot.Response.Scenario == "charge";
        bool changed = observed.Any(s => s.OnAc is bool ac && ac != expectedAc);
        bool interrupted = breaks.Any(t => t > origin && t <= actual.TimestampUtc);
        for (int i = 1; i < observed.Length; i++)
            interrupted |= observed[i].TimestampUtc - observed[i-1].TimestampUtc > TimeSpan.FromMinutes(3);
        return new(actual, predicted, changed, interrupted);
    }

    public static TrendPoint? Latest(ForecastSnapshot? snapshot, IReadOnlyList<BatterySample> history,
        IReadOnlyList<DateTimeOffset> breaks) => history.Reverse().Select(s => At(snapshot, s, history, breaks)).FirstOrDefault(p => p is not null);

    public static string Describe(TrendPoint point) => L.F("TrendPoint", point.Actual.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm", L.Culture),
        point.Actual.LevelPct, point.Predicted.P50, point.Difference) +
        (point.ScenarioChanged ? " · " + L.T("TrendAcChanged") : "") +
        (point.Interrupted ? " · " + L.T("TrendInterrupted") : "");
}
