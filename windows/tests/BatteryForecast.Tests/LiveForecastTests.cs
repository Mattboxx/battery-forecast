using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Tests;

public class LiveForecastTests
{
    static readonly DateTimeOffset Zero=DateTimeOffset.FromUnixTimeSeconds(1800000000);

    /// <summary>Model forecast from <paramref name="level"/> at <paramref name="perHour"/> %/h (band ±20% of the drain), reserve 10%, stopped at 0%.</summary>
    static ForecastResponse Model(double level,double perHour)
    {
        var points=Enumerable.Range(1,288).Select(k=>
        {
            double At(double rate)=>Math.Max(0,level-rate*k/12);
            return new CurvePointDto(Zero.AddMinutes(5*k).ToUnixTimeSeconds(),At(perHour*1.2),At(perHour),At(perHour*.8));
        }).ToList();
        return new() { Source="timestral",Scenario="no_charge",TimeBasis="active_use",Method="drain",ContextEndUtc=Zero.ToUnixTimeSeconds(),Level=level,ReservePct=10,
            Quality=new() { State="ok" },Curve=points,Crossing=new() { Status="within_horizon",CentralUtc=Zero.AddHours((level-10)/perHour).ToUnixTimeSeconds() } };
    }
    static double Minutes(ForecastResponse r,double? t)=>(t!.Value-r.ContextEndUtc)/60;

    [Fact]
    public void TheCurveRestartsFromTheLevelJustReadAndKeepsTheForecastDrain()
    {
        var model=Model(70,20);                                             // 10% after 3 hours
        // half an hour later the model's curve is at 60%, the battery at 56%: it drained faster than forecast
        var live=LiveForecast.Anchor(model,Zero.AddMinutes(30),56)!;
        Assert.Equal(Zero.AddMinutes(30).ToUnixTimeSeconds(),live.ContextEndUtc);Assert.Equal(56,live.Level);
        Assert.Equal((56-10)/20.0*60,Minutes(live,live.Crossing.CentralUtc),6);     // 138 minutes from now, not 150
        Assert.Equal((56-10)/24.0*60,Minutes(live,live.Crossing.EarlyUtc),6);
        Assert.Equal((56-10)/16.0*60,Minutes(live,live.Crossing.LateUtc),6);
        Assert.Equal(56-20/12.0,live.Curve[0].P50,9);                       // the band opens again from the reading
        Assert.All(live.Curve,p=>Assert.True(p.P10<=p.P50 && p.P50<=p.P90 && p.P90<=56));
        Assert.Null(live.RawCurve);Assert.Null(live.Blend);
    }

    [Fact]
    public void ABatteryThatOutlastsTheForecastStillGetsAnArrival()
    {
        var model=Model(30,60);                                             // the model: empty after 30 minutes
        // 20 minutes later the model's curve is at 10% and reaches 0% in 10 more minutes; the battery is still at 25%
        var live=LiveForecast.Anchor(model,Zero.AddMinutes(20),25)!;
        Assert.Equal("within_horizon",live.Crossing.Status);
        Assert.Equal((25-10)/60.0*60,Minutes(live,live.Crossing.CentralUtc),6);     // the forecast drain goes on past the model's 0%
    }

    [Fact]
    public void NothingToFollowBeforeTheForecastOrBelowTheReserve()
    {
        var model=Model(70,20);
        Assert.Null(LiveForecast.Anchor(model,Zero,70));
        Assert.Null(LiveForecast.Anchor(model,Zero.AddMinutes(30),double.NaN));
        Assert.Null(LiveForecast.Anchor(model with { Scenario="charge" },Zero.AddMinutes(30),60));
        var below=LiveForecast.Anchor(model,Zero.AddMinutes(30),9)!;
        Assert.Equal("already_below",below.Crossing.Status);Assert.Null(below.Crossing.CentralUtc);
    }
}
