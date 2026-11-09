using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Core.Forecasting;

/// <summary>How the arrival at the reserve was combined, saved with each forecast.</summary>
public sealed record BlendInfo
{
    /// <summary>Weight of the measured-power estimate, 0..1; the Timestral curve has the rest.</summary>
    public double PowerWeight { get; init; }
    public double? PowerMinutes { get; init; }
    public double? TimestralMinutes { get; init; }
    /// <summary>Past arrivals at the reserve on this PC used to choose the weight; below the minimum the prior is used.</summary>
    public int Evaluations { get; init; }
    public double? PowerError { get; init; }
    public double? TimestralError { get; init; }
    /// <summary>Minutes of the current discharge behind the power estimate (it improves as the stretch grows).</summary>
    public double? PowerWindowMinutes { get; init; }
    /// <summary>Learned weight for each length of measured stretch (see <see cref="EstimateBlend.Stretches"/>), null when not enough evidence.</summary>
    public double?[]? StretchWeights { get; init; }
}

/// <summary>
/// Arrival at the reserve from two independent views, weighted by how wrong each has been on this PC.
/// The measured power (energy left / mean power of the current stretch) knows the present load exactly but not what
/// comes next. The Timestral curve forecast from the level came out more than an hour late on heavy discharges, so
/// with it the power estimate leads; the curve integrated from Timestral's forecast of the drain (since 0.9.9) was
/// closer to reality than the power estimate, so with it Timestral leads. Every PC then learns its own weight from
/// the arrivals it has actually observed, only from forecasts made the same way.
/// The Timestral curve is then retimed to the combined arrival, so chart, headline and alerts agree.
/// </summary>
public static class EstimateBlend
{
    public const double PriorPowerWeight=.9,PriorDrainPowerWeight=.3,MinimumWeight=.05,MaximumWeight=.97;
    /// <summary>The weight of the measured power before this PC has enough evidence of its own.</summary>
    public static double Prior(bool drain)=>drain?PriorDrainPowerWeight:PriorPowerWeight;
    /// <summary>Upper bounds, in minutes, of the measured-stretch lengths that get their own weight.</summary>
    public static readonly double[] Stretches={10,30,double.PositiveInfinity};
    public const int MinimumEvaluations=5;
    static readonly TimeSpan Thinning=TimeSpan.FromMinutes(10),MaximumGap=TimeSpan.FromMinutes(3),Lookback=TimeSpan.FromMinutes(70);

    /// <summary>Errors of both estimates at past forecasts whose arrival at the reserve was observed, before <paramref name="now"/>.</summary>
    public static BlendInfo Learn(IReadOnlyList<ForecastSnapshot> saved,IReadOnlyList<BatterySample> observations,DateTimeOffset now,
        List<(DateTimeOffset Origin,double Actual,double Power,double Timestral)>? trace=null,bool drain=false)
    {
        var samples=observations.Where(s=>s.Source=="windows" && s.LevelPct is not null && s.TimestampUtc<=now).OrderBy(s=>s.TimestampUtc).ToArray();
        var power=new List<double>();var timestral=new List<double>();var stretch=new List<int>();DateTimeOffset? last=null;
        foreach(var snapshot in saved.OrderBy(s=>s.IssuedAt))
        {
            var r=snapshot.Response;
            if(snapshot.Source!="timestral" || r.Scenario!="no_charge" || r.Conditions is { OnAc:true } || r.FromDrain!=drain) continue;
            // the Timestral arrival before any blending (saved forecasts show the combined one)
            double? central=r.Blend?.TimestralMinutes is { } own ? r.ContextEndUtc+own*60 : r.Blend is null ? r.Crossing.CentralUtc : null;
            if(central is null) continue;
            var origin=DateTimeOffset.FromUnixTimeMilliseconds((long)(r.ContextEndUtc*1000));
            if(last is { } previous && origin-previous<Thinning) continue;
            int first=Array.FindIndex(samples,s=>s.TimestampUtc>=origin);
            if(first<1 || (samples[first].TimestampUtc-origin).TotalSeconds>150) continue;
            DateTimeOffset? arrival=null;
            for(int k=first;k<samples.Length;k++)
            {
                if(samples[k].OnAc==true || k>first && samples[k].TimestampUtc-samples[k-1].TimestampUtc>MaximumGap) break;
                if(samples[k].LevelPct<=r.ReservePct) { arrival=samples[k].TimestampUtc;break; }
            }
            if(arrival is null) continue;
            var before=samples.Where(s=>s.TimestampUtc<=origin && origin-s.TimestampUtc<=Lookback).ToList();
            if(RecentRate.Estimate(before,r.ReservePct) is not { ReserveAt: { } estimated } recent) continue;
            double actual=(arrival.Value-origin).TotalMinutes;
            power.Add(Math.Abs((estimated-origin).TotalMinutes-actual));
            timestral.Add(Math.Abs((central.Value-r.ContextEndUtc)/60-actual));
            stretch.Add(StretchOf(recent.Window.TotalMinutes));
            trace?.Add((origin,actual,(estimated-origin).TotalMinutes,(central.Value-r.ContextEndUtc)/60));
            last=origin;
        }
        // one weight per stretch length: a power estimate from 3 minutes of data is not one from an hour
        var weights=new double?[Stretches.Length];
        for(int b=0;b<Stretches.Length;b++)
        {
            var idx=Enumerable.Range(0,power.Count).Where(i=>stretch[i]==b).ToArray();
            if(idx.Length>=MinimumEvaluations) weights[b]=Weight(idx.Average(i=>power[i]),idx.Average(i=>timestral[i]));
        }
        var info=new BlendInfo { PowerWeight=Prior(drain),Evaluations=power.Count,StretchWeights=weights };
        if(power.Count<MinimumEvaluations) return info;
        double ep=power.Average(),et=timestral.Average();
        return info with { PowerWeight=Weight(ep,et),PowerError=ep,TimestralError=et };
    }

    /// <summary>Inverse squared error: the closer estimate dominates, the other still contributes.</summary>
    static double Weight(double powerError,double timestralError)=>
        Math.Clamp(timestralError*timestralError/(timestralError*timestralError+powerError*powerError+1e-9),MinimumWeight,MaximumWeight);
    static int StretchOf(double minutes)=>Array.FindIndex(Stretches,limit=>minutes<limit);

    /// <summary>
    /// Right after unplugging there are too few readings for the power estimate: Windows' own remaining time (also
    /// based on the measured power) stands in, scaled from empty to the reserve. Null when Windows gives none.
    /// </summary>
    public static RecentEstimate? FromWindows(BatterySample now,double reserve)
    {
        if(now.OsRemainingSeconds is not int seconds || seconds<=0 || now.LevelPct is not double level || level<=reserve) return null;
        double hours=seconds/3600.0*(level-reserve)/level;
        return new(RecentMethod.Power,(level-reserve)/hours,now.TimestampUtc.AddHours(hours),TimeSpan.Zero);
    }

    /// <summary>
    /// Last resort with no reading of the current stretch at all: the typical (median) power of this PC on battery in
    /// the given history, applied to the energy left now. Null without energy readings.
    /// </summary>
    public static RecentEstimate? FromHistory(IReadOnlyList<BatterySample> history,BatterySample now,double reserve)
    {
        if(now.RemainingMWh is not int remaining || now.FullChargeMWh is not int full || full<=0) return null;
        var powers=history.Where(s=>s.IsDischarging && s.DischargePowerMW is > 0).Select(s=>(double)s.DischargePowerMW!.Value).Order().ToArray();
        if(powers.Length<30) return null;
        double mw=powers[powers.Length/2],usable=remaining-reserve/100*full;
        if(usable<=0) return null;
        return new(RecentMethod.Power,mw/full*100,now.TimestampUtc.AddHours(usable/mw),TimeSpan.Zero);
    }

    /// <summary>Retimes the curve so that its median reaches the reserve at the weighted arrival.</summary>
    public static ForecastResponse Apply(ForecastResponse r,RecentEstimate? recent,BlendInfo blend)
    {
        double origin=r.ContextEndUtc;
        double? Minutes(double? t)=>t is { } v ? (v-origin)/60 : null;
        double? timestral=Minutes(r.Crossing.CentralUtc);
        double? measured=recent?.ReserveAt is { } at ? Minutes(at.ToUnixTimeMilliseconds()/1000.0) : null;
        blend=blend with { PowerMinutes=measured,TimestralMinutes=timestral,PowerWindowMinutes=recent?.Window.TotalMinutes };
        // the weight learned for a stretch as long as the current one, if there is enough evidence for it
        if(recent is not null && blend.StretchWeights is { } byStretch && byStretch[StretchOf(recent.Window.TotalMinutes)] is { } learned)
            blend=blend with { PowerWeight=learned };
        if(r.Scenario!="no_charge" || r.Crossing.Status!="within_horizon" || timestral is not > 0 || measured is not > 0 || r.Curve.Count==0)
            return r with { Blend=blend };
        double target=blend.PowerWeight*measured.Value+(1-blend.PowerWeight)*timestral.Value;
        double speed=timestral.Value/target;                      // >1: the combined arrival is earlier
        var source=r.Curve;var points=new List<CurvePointDto>();
        for(int step=1;step<=Protocol.HorizonSteps;step++)
        {
            double t=origin+step*Protocol.StepSeconds,u=origin+(t-origin)*speed;
            if(u>source[^1].T) break;
            points.Add(Interpolate(r,source,u) with { T=t });
        }
        if(points.Count==0) return r with { Blend=blend };
        double reserve=r.ReservePct;
        double? Cross(Func<CurvePointDto,double> get)=>points.FirstOrDefault(p=>get(p)<=reserve)?.T;
        var central=Cross(p=>p.P50);
        return r with
        {
            Curve=points,Blend=blend,RawCurve=r.RawCurve??r.Curve,RawTiming=r.RawTiming??Timing(r),
            Crossing=r.Crossing with { Status=central is null?"beyond_horizon":"within_horizon",CentralUtc=central,EarlyUtc=Cross(p=>p.P10),LateUtc=Cross(p=>p.P90) },
            HabitualRechargeUtc=Warp(r.HabitualRechargeUtc),ExtrapolatedFromUtc=Warp(r.ExtrapolatedFromUtc),
            ExtrapolationRatePctPerHour=r.ExtrapolationRatePctPerHour*speed,
        };
        double? Warp(double? t)=>t is { } v ? origin+(v-origin)/speed : null;
    }

    /// <summary>The timing as the model issued it, kept so that learning never reads a retimed curve.</summary>
    internal static RawForecastTiming Timing(ForecastResponse r)=>
        new(r.Crossing,r.HabitualRechargeUtc,r.HabitualUnplugUtc,r.ExtrapolatedFromUtc,r.ExtrapolationRatePctPerHour);

    internal static CurvePointDto Interpolate(ForecastResponse r,IReadOnlyList<CurvePointDto> points,double t)
    {
        var previous=new CurvePointDto(r.ContextEndUtc,r.Level,r.Level,r.Level);
        foreach(var p in points)
        {
            if(p.T>=t)
            {
                double k=p.T<=previous.T?1:(t-previous.T)/(p.T-previous.T);
                return new(t,previous.P10+k*(p.P10-previous.P10),previous.P50+k*(p.P50-previous.P50),previous.P90+k*(p.P90-previous.P90));
            }
            previous=p;
        }
        return points[^1] with { T=t };
    }
}
