using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Tests;

public class ChargeBlendTests
{
    static readonly DateTimeOffset Zero=DateTimeOffset.FromUnixTimeSeconds(1800000000);

    /// <summary>Charge forecast from 50% at <paramref name="perHour"/> %/h, target 80%, or never reaching it.</summary>
    static ForecastResponse Forecast(double? perHour)
    {
        var points=Enumerable.Range(1,288).Select(k=>
        {
            double v=perHour is { } rate ? Math.Min(100,50+rate*k/12) : Math.Min(75,50+k*.5);
            return new CurvePointDto(Zero.AddMinutes(5*k).ToUnixTimeSeconds(),Math.Max(0,v-2),v,Math.Min(100,v+2));
        }).ToList();
        double? central=perHour is { } r ? Zero.AddHours(30/r).ToUnixTimeSeconds() : null;
        return new() { Source="timestral",Scenario="charge",TimeBasis="active_use",ContextEndUtc=Zero.ToUnixTimeSeconds(),Level=50,ReservePct=80,TargetPct=80,
            Quality=new() { State="ok" },Curve=points,Crossing=new() { Status=central is null?"beyond_horizon":"within_horizon",CentralUtc=central } };
    }

    [Fact]
    public void TheTimestralCurveIsRetimedToTheMeasuredTimeAndKeptRaw()
    {
        var forecast=Forecast(10);                                          // Timestral: 80% after 3 hours
        var measured=new ChargeEstimate(ChargeEstimateKind.PastSessions,80,Zero.AddMinutes(60),3);
        var r=ChargeBlend.Apply(forecast,measured);
        Assert.Equal(60,(r.Crossing.CentralUtc!.Value-Zero.ToUnixTimeSeconds())/60,0);
        Assert.Equal(180,r.Blend!.TimestralMinutes!.Value,3);Assert.Equal(60,r.Blend.PowerMinutes!.Value,3);
        Assert.Same(forecast.Curve,r.RawCurve);                            // learning keeps reading the model's own curve
        Assert.True(r.Crossing.EarlyUtc<=r.Crossing.CentralUtc && r.Crossing.CentralUtc<=r.Crossing.LateUtc);
        // no measurement: unchanged
        Assert.Same(forecast,ChargeBlend.Apply(forecast,new ChargeEstimate(ChargeEstimateKind.Collecting,80)));
    }

    [Fact]
    public void WithoutATimestralArrivalTheCurveIsTheMeasuredLineWithABand()
    {
        var r=ChargeBlend.Apply(Forecast(null),new ChargeEstimate(ChargeEstimateKind.RecentRate,80,Zero.AddMinutes(90)));
        Assert.Equal("within_horizon",r.Crossing.Status);
        Assert.Equal(90,(r.Crossing.CentralUtc!.Value-Zero.ToUnixTimeSeconds())/60,0);
        Assert.Equal(75,(r.Crossing.EarlyUtc!.Value-Zero.ToUnixTimeSeconds())/60,0);     // 90 × 0.8, on the 5-minute grid
        Assert.Equal(110,(r.Crossing.LateUtc!.Value-Zero.ToUnixTimeSeconds())/60,0);     // 90 × 1.2 = 108 → next step
        Assert.All(r.Curve,p=>Assert.True(p.P10<=p.P50 && p.P50<=p.P90 && p.P90<=80));
        Assert.Null(r.Blend!.TimestralMinutes);
    }

    [Fact]
    public void AnArrivalMinutesAwayNeverFallsBackToTimestralHoursAway()
    {
        // Timestral 15 hours, measured 2 minutes: the retimed curve would end before its first point
        var r=ChargeBlend.Apply(Forecast(2),new ChargeEstimate(ChargeEstimateKind.RecentRate,80,Zero.AddMinutes(2)));
        Assert.Equal(5,(r.Crossing.CentralUtc!.Value-Zero.ToUnixTimeSeconds())/60,0);   // the first 5-minute step
    }
}
