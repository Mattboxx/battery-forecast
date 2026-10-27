using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Forecasting;

public sealed record ForecastConditions
{
    public string Version { get; init; } = "active-app-v1";
    public bool OnAc { get; init; }
    public int? DesignMWh { get; init; }
    public int? FullMWh { get; init; }
    public int? CpuBand { get; init; }
    public double? DisplayState { get; init; }
    public double? BrightnessPct { get; init; }
    public bool AppContext { get; init; }
    public string? WorkloadKey { get; init; }

    public static int? Band(double? cpu)=>cpu is not { } v || !double.IsFinite(v) ? null : v<5?0:v<20?1:v<50?2:3;
    public static string? AppKey(long? profile,IReadOnlyList<ProcessLoad>? apps)
    {
        if(profile is null || apps is null || !apps.Any(p=>p.CpuPct is not null)) return null;
        var selected=apps.Where(p=>p.CpuPct>.25).OrderByDescending(p=>p.CpuPct).ThenByDescending(p=>p.WorkingSetMB).Take(3).ToArray();
        if(selected.Length==0) selected=apps.Where(p=>p.CpuPct is not null).OrderByDescending(p=>p.WorkingSetMB).Take(3).ToArray();
        return profile.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|"+
            string.Join('|',selected.Select(p=>ProcessFeatures.Id(p.Name)+":"+Band(p.CpuPct)).Order(StringComparer.Ordinal));
    }
    public static ForecastConditions Capture(BatterySample battery,SystemSample? context,bool appContext)=>new()
    {
        OnAc=battery.OnAc==true,DesignMWh=battery.DesignMWh,FullMWh=battery.FullChargeMWh,
        CpuBand=Band(context?.CpuPct),DisplayState=context?.DisplayState,BrightnessPct=context?.BrightnessPct,
        AppContext=appContext,WorkloadKey=appContext?AppKey(context?.WorkloadProfile,context?.ProcessLoads):null,
    };
}

public sealed record CalibrationInfo
{
    public bool Enabled { get; init; }
    public bool Ready { get; init; }
    public double Factor { get; init; } = 1;
    public int Windows { get; init; }
    public int Sessions { get; init; }
    public string Scope { get; init; } = "general_load";
    public string Reason { get; init; } = "collecting";
    public DateTimeOffset? ResetUtc { get; init; }
}

public sealed record CalibrationContext(DateTimeOffset At,int? CpuBand,double? DisplayState,double? BrightnessPct,string? WorkloadKey);
public sealed record RawForecastTiming(CrossingDto Crossing,double? Recharge,double? Unplug,double? Extrapolated,double? Rate);

/// <summary>Bounded local rate calibration, from immutable original forecasts and subsequent active observations.</summary>
public static class LocalCalibration
{
    public const int MinimumWindows=8,MinimumSessions=3;
    static readonly TimeSpan MaximumGap=TimeSpan.FromMinutes(3);
    static DateTimeOffset Time(double unix)=>DateTimeOffset.FromUnixTimeMilliseconds((long)(unix*1000));

    public static CalibrationInfo Learn(ForecastResponse current,IReadOnlyList<ForecastSnapshot> saved,
        IReadOnlyList<BatterySample> observations,IReadOnlyList<DateTimeOffset> interruptions,
        IReadOnlyList<CalibrationContext> context,DateTimeOffset now,DateTimeOffset? reset=null)
    {
        var conditions=current.Conditions;
        var empty=new CalibrationInfo { Enabled=true,ResetUtc=reset,Scope=conditions?.AppContext==true?"same_apps_and_load":"general_load" };
        if(conditions is null || current.Quality.State is not ("ok" or "learning") || current.TimeBasis!="active_use" || string.IsNullOrWhiteSpace(current.ModelRevision) || current.ModelRevision=="unknown") return empty;
        if(conditions.AppContext && conditions.WorkloadKey is null) return empty with { Reason="fresh_load_needed" };
        var samples=observations.Where(s=>s.TimestampUtc<=now && s.Source=="windows").OrderBy(s=>s.TimestampUtc).ToArray();
        var breaks=interruptions.Where(t=>t<=now).ToList();
        for(int i=1;i<samples.Length;i++)
            if(samples[i].OnAc!=samples[i-1].OnAc || samples[i].TimestampUtc-samples[i-1].TimestampUtc>MaximumGap ||
                samples[i].DesignMWh!=samples[i-1].DesignMWh) breaks.Add(samples[i].TimestampUtc);
        breaks.Sort();
        var accepted=new List<(DateTimeOffset End,double Ratio,DateTimeOffset Session)>();
        DateTimeOffset? usedUntil=null;
        foreach(var snapshot in saved.OrderBy(s=>s.View.Generated))
        {
            var r=snapshot.Response;var origin=Time(r.ContextEndUtc);
            if(origin>=now || snapshot.IssuedAt>now || (reset is { } cutoff && snapshot.IssuedAt<cutoff) ||
                (reset is { } resetOrigin && origin<resetOrigin) ||
                !snapshot.Source.StartsWith("timestral",StringComparison.Ordinal) || snapshot.Source.EndsWith(":held",StringComparison.Ordinal) ||
                r.TimeBasis!="active_use" || r.Quality.State is not ("ok" or "learning") || r.Scenario!=current.Scenario || r.ModelRevision!=current.ModelRevision ||
                r.Conditions is not { } past || !Comparable(past,conditions) || usedUntil is { } previousEnd && origin<previousEnd) continue;
            bool charging=r.Scenario=="charge";
            if(past.OnAc!=charging) continue; // an unplug-now forecast issued on AC has no matching realised scenario
            foreach(int minutes in new[]{30,60,120})
            {
                var target=origin.AddMinutes(minutes);
                if(target>now) continue;
                var index=LowerBound(samples,target);
                if(index==samples.Length) index--;
                if(index<0) continue;
                if(index>0 && Math.Abs((samples[index-1].TimestampUtc-target).TotalSeconds)<Math.Abs((samples[index].TimestampUtc-target).TotalSeconds)) index--;
                var actual=samples[index];
                if(Math.Abs((actual.TimestampUtc-target).TotalSeconds)>90 || actual.TimestampUtc<=snapshot.IssuedAt || actual.LevelPct is not { } actualLevel) continue;
                var expected=At(r,r.RawCurve??r.Curve,actual.TimestampUtc);
                if(expected is not { } prediction) continue;
                double predictedDelta=charging?prediction.P50-r.Level:r.Level-prediction.P50;
                double actualDelta=charging?actualLevel-r.Level:r.Level-actualLevel;
                if(predictedDelta<2 || actualDelta<2 || (charging && (actualLevel>=99||prediction.P50>=99)) ||
                    (!charging && (actualLevel<=r.ReservePct+.5||prediction.P50<=r.ReservePct+.5))) continue;
                int first=LowerBound(samples,origin);
                if(first>0 && origin-samples[first-1].TimestampUtc<=TimeSpan.FromSeconds(90)) first--;
                if(first>index || Math.Abs((samples[first].TimestampUtc-origin).TotalSeconds)>90 || index-first<5) continue;
                bool valid=true;
                for(int j=first;j<=index;j++)
                {
                    var s=samples[j];
                    if(s.OnAc!=charging || s.LevelPct is not { } level || (charging && s.State!=ChargeState.Charging) ||
                        past.DesignMWh is { } design && s.DesignMWh is { } actualDesign && design!=actualDesign ||
                        (j>first && (s.TimestampUtc-samples[j-1].TimestampUtc>MaximumGap ||
                        Math.Abs(level-samples[j-1].LevelPct.GetValueOrDefault(level))>5))) { valid=false;break; }
                }
                if(!valid || breaks.Any(t=>t>origin && t<=actual.TimestampUtc) || !StableContext(past,context,origin,actual.TimestampUtc)) continue;
                double ratio=actualDelta/predictedDelta;
                if(!double.IsFinite(ratio)||ratio<=0) continue;
                var session=breaks.LastOrDefault(t=>t<=origin);
                if(session==default && samples.Length>0) session=samples[0].TimestampUtc;
                accepted.Add((actual.TimestampUtc,ratio,session));usedUntil=actual.TimestampUtc;break;
            }
        }
        int sessions=accepted.Select(p=>p.Session).Distinct().Count();
        var report=empty with { Windows=accepted.Count,Sessions=sessions };
        if(accepted.Count<MinimumWindows || sessions<MinimumSessions) return report;
        var ratios=accepted.Select(p=>p.Ratio).Order().ToArray();
        double median=ratios.Length%2==1?ratios[ratios.Length/2]:(ratios[ratios.Length/2-1]+ratios[ratios.Length/2])/2;
        return report with { Ready=true,Reason="ready",Factor=Math.Clamp(median,.75,1.25) };
    }

    static bool Comparable(ForecastConditions past,ForecastConditions current)
    {
        if(past.Version!=current.Version || past.OnAc!=current.OnAc || past.DesignMWh!=current.DesignMWh ||
            past.CpuBand!=current.CpuBand || past.DisplayState!=current.DisplayState || past.AppContext!=current.AppContext) return false;
        if(past.FullMWh is >0 && current.FullMWh is >0 && Math.Abs(past.FullMWh.Value-current.FullMWh.Value)>current.FullMWh.Value*.05) return false;
        if(current.BrightnessPct is { } light && (past.BrightnessPct is not { } old || Math.Abs(light-old)>10)) return false;
        return !current.AppContext || past.WorkloadKey==current.WorkloadKey;
    }
    static bool StableContext(ForecastConditions expected,IReadOnlyList<CalibrationContext> all,DateTimeOffset from,DateTimeOffset until)
    {
        if(expected.CpuBand is null && expected.DisplayState is null && expected.BrightnessPct is null && !expected.AppContext) return true;
        var rows=all.Where(c=>c.At>=from&&c.At<=until).OrderBy(c=>c.At).ToArray();
        if(rows.Length<6 || rows[0].At-from>TimeSpan.FromSeconds(150) || until-rows[^1].At>TimeSpan.FromSeconds(150)) return false;
        for(int i=1;i<rows.Length;i++) if(rows[i].At-rows[i-1].At>TimeSpan.FromSeconds(150)) return false;
        return rows.Count(c=>(expected.CpuBand is null||c.CpuBand==expected.CpuBand) &&
            (expected.DisplayState is null||c.DisplayState==expected.DisplayState) &&
            (expected.BrightnessPct is null||c.BrightnessPct is { } b && Math.Abs(b-expected.BrightnessPct.Value)<=10) &&
            (!expected.AppContext||c.WorkloadKey==expected.WorkloadKey)) >= Math.Ceiling(rows.Length*.8);
    }
    static int LowerBound(IReadOnlyList<BatterySample> values,DateTimeOffset t)
    {
        int low=0,high=values.Count;while(low<high) { int mid=(low+high)/2;if(values[mid].TimestampUtc<t) low=mid+1;else high=mid; }return low;
    }
    static CurvePointDto? At(ForecastResponse response,IReadOnlyList<CurvePointDto> points,DateTimeOffset time)
    {
        double t=time.ToUnixTimeMilliseconds()/1000.0;
        if(points.Count==0 || t<response.ContextEndUtc || t>points[^1].T) return null;
        var previous=new CurvePointDto(response.ContextEndUtc,response.Level,response.Level,response.Level);
        foreach(var p in points)
        {
            if(p.T>=t)
            {
                double k=p.T<=previous.T?1:(t-previous.T)/(p.T-previous.T);
                return new(t,previous.P10+k*(p.P10-previous.P10),previous.P50+k*(p.P50-previous.P50),previous.P90+k*(p.P90-previous.P90));
            }
            previous=p;
        }
        return null;
    }
    public static ForecastResponse Apply(ForecastResponse raw,CalibrationInfo report)
    {
        var original=raw.RawCurve??raw.Curve;
        var saved=original.ToArray();
        var timing=raw.RawTiming??new(raw.Crossing,raw.HabitualRechargeUtc,raw.HabitualUnplugUtc,raw.ExtrapolatedFromUtc,raw.ExtrapolationRatePctPerHour);
        raw=raw with { Curve=original.ToList(),RawCurve=null,RawTiming=null,Calibration=report,Crossing=timing.Crossing,
            HabitualRechargeUtc=timing.Recharge,HabitualUnplugUtc=timing.Unplug,ExtrapolatedFromUtc=timing.Extrapolated,ExtrapolationRatePctPerHour=timing.Rate };
        if(!double.IsFinite(report.Factor)||report.Factor<=0) return raw with { Calibration=report with { Ready=false,Factor=1,Reason="collecting" } };
        report=report with { Factor=Math.Clamp(report.Factor,.75,1.25) };raw=raw with { Calibration=report };
        if(!report.Enabled || !report.Ready || Math.Abs(report.Factor-1)<1e-6 || original.Count==0) return raw;
        var points=new List<CurvePointDto>();
        for(int step=1;step<=Protocol.HorizonSteps;step++)
        {
            double t=raw.ContextEndUtc+step*Protocol.StepSeconds;
            var point=At(raw,original,Time(raw.ContextEndUtc+(t-raw.ContextEndUtc)*report.Factor));
            if(point is null) break; // do not invent a raw forecast beyond its supported horizon
            points.Add(point with { T=t });
        }
        if(points.Count==0) return raw with { RawCurve=saved,Calibration=report with { Ready=false,Reason="collecting",Factor=1 } };
        bool charging=raw.Scenario=="charge";double target=charging?raw.TargetPct??raw.ReservePct:raw.ReservePct;
        double? Cross(Func<CurvePointDto,double> get)=>points.FirstOrDefault(p=>charging?get(p)>=target:get(p)<=target)?.T;
        var central=Cross(p=>p.P50);
        double? Warp(double? t)=>t is { } value ? raw.ContextEndUtc+(value-raw.ContextEndUtc)/report.Factor : null;
        return raw with
        {
            Curve=points,RawCurve=saved,RawTiming=timing,Calibration=report,
            Crossing=raw.Crossing.Status is "already_below" or "already_reached" ? raw.Crossing : raw.Crossing with {
                Status=central is null?"beyond_horizon":"within_horizon",CentralUtc=central,
                EarlyUtc=Cross(p=>charging?p.P90:p.P10),LateUtc=Cross(p=>charging?p.P10:p.P90) },
            HabitualRechargeUtc=Warp(raw.HabitualRechargeUtc),HabitualUnplugUtc=Warp(raw.HabitualUnplugUtc),ExtrapolatedFromUtc=Warp(raw.ExtrapolatedFromUtc),
            ExtrapolationRatePctPerHour=raw.ExtrapolationRatePctPerHour*report.Factor,
        };
    }
}
