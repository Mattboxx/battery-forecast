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
    /// <summary>
    /// Battery levels (band midpoints) with the learned speed factor for each: on many laptops the reported
    /// percentage falls much faster near empty than the model expects, so one factor for the whole curve is
    /// not enough. Null in reports saved by earlier versions, which used <see cref="Factor"/> alone.
    /// </summary>
    public double[]? LevelPoints { get; init; }
    public double[]? LevelFactors { get; init; }
}

public sealed record CalibrationContext(DateTimeOffset At,int? CpuBand,double? DisplayState,double? BrightnessPct,string? WorkloadKey);
public sealed record RawForecastTiming(CrossingDto Crossing,double? Recharge,double? Unplug,double? Extrapolated,double? Rate);

/// <summary>
/// Bounded local rate calibration, from immutable original forecasts and subsequent active observations.
/// Evidence is searched from the most specific to the broadest comparable conditions: exact app workload and
/// load first, then the same CPU load and screen, a similar load, and finally any comparable battery use.
/// Requiring identical app workloads alone almost never finds two comparable moments (the top apps and their
/// CPU bands change all the time), so learning would never start; the first level with enough independent
/// windows and sessions is used, and its scope is reported.
/// </summary>
public static class LocalCalibration
{
    public const int MinimumWindows=8,MinimumSessions=3;
    static readonly TimeSpan MaximumGap=TimeSpan.FromMinutes(3);
    static DateTimeOffset Time(double unix)=>DateTimeOffset.FromUnixTimeMilliseconds((long)(unix*1000));

    /// <summary>How closely a past forecast's conditions must match the current ones.</summary>
    public enum Match { SameAppsAndLoad, SameLoad, SimilarLoad, AnyUse }
    public static string ScopeOf(Match level)=>level switch
    {
        Match.SameAppsAndLoad=>"same_apps_and_load", Match.SameLoad=>"same_load", Match.SimilarLoad=>"similar_load", _=>"all_use",
    };

    public static CalibrationInfo Learn(ForecastResponse current,IReadOnlyList<ForecastSnapshot> saved,
        IReadOnlyList<BatterySample> observations,IReadOnlyList<DateTimeOffset> interruptions,
        IReadOnlyList<CalibrationContext> context,DateTimeOffset now,DateTimeOffset? reset=null)
    {
        var conditions=current.Conditions;
        var empty=new CalibrationInfo { Enabled=true,ResetUtc=reset,Scope=ScopeOf(conditions?.AppContext==true?Match.SameAppsAndLoad:Match.SameLoad) };
        if(conditions is null || current.Quality.State is not ("ok" or "learning") || current.TimeBasis!="active_use" || string.IsNullOrWhiteSpace(current.ModelRevision) || current.ModelRevision=="unknown") return empty;
        var samples=observations.Where(s=>s.TimestampUtc<=now && s.Source=="windows").OrderBy(s=>s.TimestampUtc).ToArray();
        var breaks=interruptions.Where(t=>t<=now).ToList();
        for(int i=1;i<samples.Length;i++)
            if(samples[i].OnAc!=samples[i-1].OnAc || samples[i].TimestampUtc-samples[i-1].TimestampUtc>MaximumGap ||
                samples[i].DesignMWh!=samples[i-1].DesignMWh) breaks.Add(samples[i].TimestampUtc);
        breaks.Sort();
        // exact app matching needs a fresh workload reading for the current forecast
        var results=Enum.GetValues<Match>()
            .Where(level=>level!=Match.SameAppsAndLoad || conditions.AppContext && conditions.WorkloadKey is not null)
            .Select(level=>Collect(level,current,conditions,saved,samples,breaks,context,now,reset,empty)).ToList();
        for(int i=0;i<results.Count;i++)
            if(results[i].Report.Ready) return WithBands(results[i].Report,results[i].Known,results.Skip(i+1).Select(r=>r.Known));
        return results.Count>0?results[^1].Report:empty;      // not ready anywhere: show the progress of the broadest search
    }

    /// <summary>
    /// Battery level bands (upper bounds) whose crossing time is compared between forecast and reality. On real
    /// heavy discharges the 20-10% band went by 2.5 times faster than forecast and the middle bands about 1.15 times:
    /// one factor learned on the first 30 minutes could not fix the arrival at the reserve, often an hour late.
    /// </summary>
    public static readonly double[] Bands={100,90,80,70,60,50,40,30,20,10};
    const double BandWidth=10,Span=5,MinimumTravel=4;
    /// <summary>Bounds of a learned speed factor (real speed / forecast speed).</summary>
    public const double MinimumFactor=.5,MaximumFactor=2;
    const int MinimumPerBand=5;
    /// <summary>Forecasts closer than this in the same scenario repeat the same evidence: only the first is used.</summary>
    static readonly TimeSpan Thinning=TimeSpan.FromMinutes(25);

    static (CalibrationInfo Report,Dictionary<double,double> Known) Collect(Match match,ForecastResponse current,ForecastConditions conditions,IReadOnlyList<ForecastSnapshot> saved,
        BatterySample[] samples,List<DateTimeOffset> breaks,IReadOnlyList<CalibrationContext> context,DateTimeOffset now,DateTimeOffset? reset,CalibrationInfo empty)
    {
        var ratios=Bands.ToDictionary(b=>b,_=>new List<double>());
        var sessionsUsed=new List<DateTimeOffset>();
        DateTimeOffset? lastOrigin=null;
        foreach(var snapshot in saved.OrderBy(s=>s.View.Generated))
        {
            var r=snapshot.Response;var origin=Time(r.ContextEndUtc);
            if(origin>=now || snapshot.IssuedAt>now || (reset is { } cutoff && snapshot.IssuedAt<cutoff) ||
                (reset is { } resetOrigin && origin<resetOrigin) ||
                !snapshot.Source.StartsWith("timestral",StringComparison.Ordinal) || snapshot.Source.EndsWith(":held",StringComparison.Ordinal) ||
                r.TimeBasis!="active_use" || r.Quality.State is not ("ok" or "learning") || r.Scenario!=current.Scenario || r.ModelRevision!=current.ModelRevision ||
                r.Conditions is not { } past || !Comparable(match,past,conditions) || lastOrigin is { } previous && origin-previous<Thinning) continue;
            bool charging=r.Scenario=="charge";
            if(past.OnAc!=charging) continue; // an unplug-now forecast issued on AC has no matching realised scenario
            int first=LowerBound(samples,origin);
            if(first>0 && origin-samples[first-1].TimestampUtc<=TimeSpan.FromSeconds(90)) first--;
            if(first>=samples.Length || Math.Abs((samples[first].TimestampUtc-origin).TotalSeconds)>90) continue;
            // the realised path: same scenario and battery, no gaps, jumps or interruptions, nothing after now
            int last=first;
            for(int j=first;j<samples.Length;j++)
            {
                var s=samples[j];
                if(s.TimestampUtc>now || s.OnAc!=charging || s.LevelPct is not { } level || (charging && s.State!=ChargeState.Charging) ||
                    past.DesignMWh is { } design && s.DesignMWh is { } actualDesign && design!=actualDesign) break;
                if(j>first && (s.TimestampUtc-samples[j-1].TimestampUtc>MaximumGap || Math.Abs(level-samples[j-1].LevelPct.GetValueOrDefault(level))>5 ||
                    breaks.Any(t=>t>samples[j-1].TimestampUtc && t<=s.TimestampUtc))) break;
                last=j;
            }
            if(last-first<5) continue;
            var curve=r.RawCurve??r.Curve;bool used=false;
            // consecutive spans of Span points from the current level, each credited to the band of its midpoint;
            // the last span may be shorter (down to the lowest level the battery reached), if at least MinimumTravel
            double reached=r.Level;
            for(int j=first;j<=last;j++)
                if(samples[j].LevelPct is { } v) reached=charging?Math.Max(reached,v):Math.Min(reached,v);
            double limit=charging?Math.Min(reached,99):Math.Max(reached,r.ReservePct);
            for(double from=r.Level;charging?limit-from>=MinimumTravel:from-limit>=MinimumTravel;from=charging?from+Span:from-Span)
            {
                double to=charging?Math.Min(from+Span,limit):Math.Max(from-Span,limit);
                double top=Math.Clamp(Math.Ceiling((from+to)/2/BandWidth)*BandWidth,BandWidth,100);
                var predictedStart=from==r.Level?origin:CurveReaches(r,curve,from,charging);
                var actualStart=from==r.Level?origin:PathReaches(samples,first,last,origin,r.Level,from,charging);
                if(predictedStart is not { } ps || actualStart is not { } aS) break;
                if(CurveReaches(r,curve,to,charging) is not { } pe || PathReaches(samples,first,last,origin,r.Level,to,charging) is not { } ae) break;
                if(ae<=snapshot.IssuedAt || ae<=aS || pe<=ps || !StableContext(match,past,context,origin,ae)) break;
                double ratio=(pe-ps).TotalSeconds/(ae-aS).TotalSeconds;
                if(!double.IsFinite(ratio)||ratio<=0) break;
                ratios[top].Add(ratio);used=true;
            }
            if(!used) continue;
            var session=breaks.LastOrDefault(t=>t<=origin);
            if(session==default && samples.Length>0) session=samples[0].TimestampUtc;
            sessionsUsed.Add(session);lastOrigin=origin;
        }
        int sessions=sessionsUsed.Distinct().Count();
        var report=empty with { Windows=sessionsUsed.Count,Sessions=sessions,Scope=ScopeOf(match),Reason="collecting" };
        // bands with enough evidence at this level: also used to fill bands missing at narrower levels
        var known=Bands.Where(b=>ratios[b].Count>=MinimumPerBand).ToDictionary(b=>b,b=>Bound(Median(ratios[b])));
        var all=ratios.Values.SelectMany(v=>v).ToList();
        if(sessionsUsed.Count<MinimumWindows || sessions<MinimumSessions || all.Count==0) return (report,known);
        return (report with { Ready=true,Reason="ready",Factor=Bound(Median(all)) },known);
    }

    /// <summary>
    /// One factor per band: from the chosen level when it has enough evidence there, otherwise from the closest broader
    /// level that has (the speed-up near empty depends on the battery more than on the load), otherwise from the nearest
    /// band of the chosen level, otherwise the overall factor.
    /// </summary>
    static CalibrationInfo WithBands(CalibrationInfo report,Dictionary<double,double> own,IEnumerable<Dictionary<double,double>> broader)
    {
        var points=new List<double>();var factors=new List<double>();
        foreach(var top in Bands.Reverse())
        {
            double factor=own.TryGetValue(top,out var mine)?mine:
                broader.FirstOrDefault(b=>b.ContainsKey(top)) is { } wider?wider[top]:
                own.Count>0?own.MinBy(k=>Math.Abs(k.Key-top)).Value:report.Factor;
            points.Add(top-BandWidth/2);factors.Add(factor);
        }
        return report with { LevelPoints=[..points],LevelFactors=[..factors] };
    }

    static double Bound(double factor)=>Math.Clamp(factor,MinimumFactor,MaximumFactor);
    static double Median(List<double> values)
    {
        var sorted=values.Order().ToArray();
        return sorted.Length%2==1?sorted[sorted.Length/2]:(sorted[sorted.Length/2-1]+sorted[sorted.Length/2])/2;
    }
    /// <summary>When the forecast median first reaches <paramref name="target"/>, interpolated between points.</summary>
    static DateTimeOffset? CurveReaches(ForecastResponse r,IReadOnlyList<CurvePointDto> curve,double target,bool charging)
    {
        double t0=r.ContextEndUtc,v0=r.Level;
        foreach(var p in curve)
        {
            if(charging?p.P50>=target:p.P50<=target)
                return Time(p.P50==v0?p.T:t0+(target-v0)/(p.P50-v0)*(p.T-t0));
            (t0,v0)=(p.T,p.P50);
        }
        return null;
    }
    /// <summary>When the real battery first reached <paramref name="target"/> along a validated path.</summary>
    static DateTimeOffset? PathReaches(BatterySample[] samples,int first,int last,DateTimeOffset origin,double start,double target,bool charging)
    {
        var (t0,v0)=(origin,start);
        for(int j=first;j<=last;j++)
        {
            if(samples[j].TimestampUtc<=origin || samples[j].LevelPct is not { } v) continue;
            if(charging?v>=target:v<=target)
                return v==v0?samples[j].TimestampUtc:t0+(samples[j].TimestampUtc-t0)*((target-v0)/(v-v0));
            (t0,v0)=(samples[j].TimestampUtc,v);
        }
        return null;
    }

    static bool Comparable(Match level,ForecastConditions past,ForecastConditions current)
    {
        // always: same model of conditions, same power scenario and the same battery
        if(past.Version!=current.Version || past.OnAc!=current.OnAc || past.DesignMWh!=current.DesignMWh) return false;
        if(past.FullMWh is >0 && current.FullMWh is >0 && Math.Abs(past.FullMWh.Value-current.FullMWh.Value)>current.FullMWh.Value*.05) return false;
        switch(level)
        {
            case Match.SameAppsAndLoad:
                if(past.CpuBand!=current.CpuBand || past.DisplayState!=current.DisplayState || past.AppContext!=current.AppContext) return false;
                if(current.BrightnessPct is { } light && (past.BrightnessPct is not { } old || Math.Abs(light-old)>10)) return false;
                return past.WorkloadKey==current.WorkloadKey;
            case Match.SameLoad:
                if(past.CpuBand!=current.CpuBand || past.DisplayState!=current.DisplayState) return false;
                return current.BrightnessPct is not { } now || past.BrightnessPct is not { } then || Math.Abs(now-then)<=20;
            case Match.SimilarLoad:
                if(current.CpuBand is { } band && past.CpuBand is { } pastBand && Math.Abs(band-pastBand)>1) return false;
                return current.DisplayState is null || past.DisplayState is null || past.DisplayState==current.DisplayState;
            default:
                return true;
        }
    }
    static bool StableContext(Match level,ForecastConditions expected,IReadOnlyList<CalibrationContext> all,DateTimeOffset from,DateTimeOffset until)
    {
        if(level==Match.AnyUse) return true;
        bool apps=level==Match.SameAppsAndLoad && expected.AppContext;
        double? brightness=level==Match.SimilarLoad?null:expected.BrightnessPct;
        if(expected.CpuBand is null && expected.DisplayState is null && brightness is null && !apps) return true;
        var rows=all.Where(c=>c.At>=from&&c.At<=until).OrderBy(c=>c.At).ToArray();
        if(rows.Length<6 || rows[0].At-from>TimeSpan.FromSeconds(150) || until-rows[^1].At>TimeSpan.FromSeconds(150)) return false;
        for(int i=1;i<rows.Length;i++) if(rows[i].At-rows[i-1].At>TimeSpan.FromSeconds(150)) return false;
        int tolerance=level==Match.SimilarLoad?1:0;
        double light=level==Match.SameAppsAndLoad?10:20, share=level switch { Match.SameAppsAndLoad=>.8, Match.SameLoad=>.7, _=>.6 };
        return rows.Count(c=>(expected.CpuBand is null||c.CpuBand is { } band && Math.Abs(band-expected.CpuBand.Value)<=tolerance) &&
            (expected.DisplayState is null||c.DisplayState==expected.DisplayState) &&
            (brightness is null||c.BrightnessPct is { } b && Math.Abs(b-brightness.Value)<=light) &&
            (!apps||c.WorkloadKey==expected.WorkloadKey)) >= Math.Ceiling(rows.Length*share);
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
        report=report with { Factor=Bound(report.Factor) };raw=raw with { Calibration=report };
        // speed factor by battery level of the forecast median; reports from earlier versions have one factor only
        double[] steps=[0],factors=[report.Factor];
        if(report.LevelPoints is { Length:>0 } p && report.LevelFactors is { } f && f.Length==p.Length && f.All(double.IsFinite))
            (steps,factors)=(p,[..f.Select(Bound)]);
        if(!report.Enabled || !report.Ready || factors.All(x=>Math.Abs(x-1)<1e-6) || original.Count==0) return raw;
        double origin=raw.ContextEndUtc;
        double FactorAt(double level)
        {
            if(level<=steps[0]) return factors[0];
            for(int i=1;i<steps.Length;i++)
                if(level<=steps[i]) return factors[i-1]+(level-steps[i-1])/(steps[i]-steps[i-1])*(factors[i]-factors[i-1]);
            return factors[^1];
        }
        // raw time -> corrected time: each stretch of the curve goes faster or slower by the factor of its level
        var rawTimes=new List<double>{origin};var newTimes=new List<double>{origin};double previousLevel=raw.Level;
        foreach(var point in original)
        {
            if(point.T<=rawTimes[^1]) continue;
            double factor=FactorAt((point.P50+(newTimes.Count==1?raw.Level:previousLevel))/2);previousLevel=point.P50;
            double corrected=newTimes[^1]+(point.T-rawTimes[^1])/factor;
            rawTimes.Add(point.T);newTimes.Add(corrected);
        }
        double? ToRaw(double t)
        {
            for(int i=1;i<newTimes.Count;i++)
                if(t<=newTimes[i]) return rawTimes[i-1]+(t-newTimes[i-1])/(newTimes[i]-newTimes[i-1])*(rawTimes[i]-rawTimes[i-1]);
            return null;     // beyond the raw horizon: never invent a forecast
        }
        double? Warp(double? value)
        {
            if(value is not { } v) return null;
            for(int i=1;i<rawTimes.Count;i++)
                if(v<=rawTimes[i]) return newTimes[i-1]+(v-rawTimes[i-1])/(rawTimes[i]-rawTimes[i-1])*(newTimes[i]-newTimes[i-1]);
            return newTimes[^1]+(v-rawTimes[^1])/FactorAt(original[^1].P50);
        }
        var points=new List<CurvePointDto>();
        for(int step=1;step<=Protocol.HorizonSteps;step++)
        {
            double t=origin+step*Protocol.StepSeconds;
            if(ToRaw(t) is not { } u || At(raw,original,Time(u)) is not { } point) break;
            points.Add(point with { T=t });
        }
        if(points.Count==0) return raw with { RawCurve=saved,Calibration=report with { Ready=false,Reason="collecting",Factor=1 } };
        bool charging=raw.Scenario=="charge";double target=charging?raw.TargetPct??raw.ReservePct:raw.ReservePct;
        double? Cross(Func<CurvePointDto,double> get)=>points.FirstOrDefault(p=>charging?get(p)>=target:get(p)<=target)?.T;
        var central=Cross(p=>p.P50);
        return raw with
        {
            Curve=points,RawCurve=saved,RawTiming=timing,Calibration=report,
            Crossing=raw.Crossing.Status is "already_below" or "already_reached" ? raw.Crossing : raw.Crossing with {
                Status=central is null?"beyond_horizon":"within_horizon",CentralUtc=central,
                EarlyUtc=Cross(p=>charging?p.P90:p.P10),LateUtc=Cross(p=>charging?p.P10:p.P90) },
            HabitualRechargeUtc=Warp(raw.HabitualRechargeUtc),HabitualUnplugUtc=Warp(raw.HabitualUnplugUtc),ExtrapolatedFromUtc=Warp(raw.ExtrapolatedFromUtc),
            ExtrapolationRatePctPerHour=raw.ExtrapolationRatePctPerHour*FactorAt(raw.Level),
        };
    }
}
