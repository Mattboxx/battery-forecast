using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Tests;

public class EstimateBlendTests
{
    static readonly DateTimeOffset Zero=DateTimeOffset.FromUnixTimeSeconds(1800000000);

    /// <summary>Forecast from <paramref name="level"/> at <paramref name="perHour"/> %/h, reserve 10%.</summary>
    static ForecastResponse Forecast(DateTimeOffset origin,double level,double perHour)
    {
        var points=Enumerable.Range(1,288).Select(k=>
        {
            double v=Math.Max(0,level-perHour*k/12);
            return new CurvePointDto(origin.AddMinutes(5*k).ToUnixTimeSeconds(),Math.Max(0,v-2),v,Math.Min(100,v+2));
        }).ToList();
        double hours=(level-10)/perHour;
        return new() { Source="timestral",Scenario="no_charge",TimeBasis="active_use",ContextEndUtc=origin.ToUnixTimeSeconds(),Level=level,ReservePct=10,
            Quality=new() { State="ok" },Curve=points,Crossing=new() { Status="within_horizon",CentralUtc=origin.AddHours(hours).ToUnixTimeSeconds() },
            Conditions=new() { OnAc=false } };
    }

    [Fact]
    public void TheCurveIsRetimedToTheWeightedArrivalAndKeepsItsOwnTimestralArrival()
    {
        var forecast=Forecast(Zero,90,20);                                  // Timestral: 10% after 4 hours
        var power=new RecentEstimate(RecentMethod.Power,40,Zero.AddHours(2),TimeSpan.FromMinutes(40));   // measured: after 2 hours
        var blended=EstimateBlend.Apply(forecast,power,new BlendInfo { PowerWeight=.75 });
        double minutes=(blended.Crossing.CentralUtc!.Value-Zero.ToUnixTimeSeconds())/60;
        Assert.Equal(.75*120+.25*240,minutes,0);                           // 150 minutes
        Assert.Equal(240,blended.Blend!.TimestralMinutes!.Value,3);Assert.Equal(120,blended.Blend.PowerMinutes!.Value,3);
        Assert.All(blended.Curve,p=>Assert.True(p.P10<=p.P50&&p.P50<=p.P90));
        // no measurement: the Timestral forecast is left as it is
        Assert.Equal(forecast.Crossing,EstimateBlend.Apply(forecast,null,new BlendInfo { PowerWeight=.75 }).Crossing);
    }

    [Fact]
    public void EachPcLearnsTheWeightFromArrivalsItActuallyObserved()
    {
        // five discharges: real 30 %/h, the power estimate knows it, Timestral says 15 %/h (arrival twice as late)
        var samples=new List<BatterySample>();var saved=new List<ForecastSnapshot>();long id=0;
        for(int day=0;day<6;day++)
        {
            var start=Zero.AddDays(day);
            for(int minute=0;minute<=200;minute++)
            {
                double level=95-30*minute/60.0;
                if(level<5) break;
                samples.Add(new(start.AddMinutes(minute),ChargeState.Discharging,level,false,RemainingMWh:(int)(level*500),FullChargeMWh:50000,DesignMWh:50000,DischargePowerMW:15000));
            }
            var origin=start.AddMinutes(30);
            saved.Add(new(++id,origin.AddSeconds(1),"timestral",Forecast(origin,80,15)));
        }
        var now=Zero.AddDays(6);
        var blend=EstimateBlend.Learn(saved,samples,now);
        Assert.Equal(6,blend.Evaluations);
        Assert.True(blend.PowerError<blend.TimestralError);
        Assert.True(blend.PowerWeight>=.9);
        // fewer observed arrivals than required: the prior weight
        Assert.Equal(EstimateBlend.PriorPowerWeight,EstimateBlend.Learn(saved.Take(2).ToList(),samples,now).PowerWeight);
    }

    [Fact]
    public void FallbacksUseWindowsThenTheTypicalPowerOfThisPc()
    {
        var now=new BatterySample(Zero,ChargeState.Discharging,60,false,RemainingMWh:30000,FullChargeMWh:50000,OsRemainingSeconds:3*3600);
        var windows=EstimateBlend.FromWindows(now,10)!;
        Assert.Equal(150,(windows.ReserveAt!.Value-Zero).TotalMinutes,3);   // 3 h to empty, 50 of 60 points to the reserve
        var history=Enumerable.Range(0,60).Select(i=>new BatterySample(Zero.AddMinutes(-60+i),ChargeState.Discharging,70,false,DischargePowerMW:10000)).ToList();
        var typical=EstimateBlend.FromHistory(history,now,10)!;
        Assert.Equal(150,(typical.ReserveAt!.Value-Zero).TotalMinutes,3);   // (30000-5000) mWh / 10000 mW
        Assert.Null(EstimateBlend.FromHistory(history.Take(5).ToList(),now,10));
    }
}
