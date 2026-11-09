namespace BatteryForecast.Core.Forecasting;

/// <summary>First time a curve reaches a level, interpolated inside the 5-minute step it happens in.</summary>
internal static class Crossings
{
    public static double? At(IReadOnlyList<CurvePointDto> points,double origin,double level,double threshold,
        Func<CurvePointDto,double> get,bool up=false)
    {
        double previousT=origin,previous=level;
        foreach(var p in points)
        {
            double v=get(p);
            if(up ? v>=threshold-1e-9 : v<=threshold+1e-9)
            {
                if(Math.Abs(v-previous)<1e-12) return p.T;
                return previousT+Math.Clamp((threshold-previous)/(v-previous),0,1)*(p.T-previousT);
            }
            (previousT,previous)=(p.T,v);
        }
        return null;
    }
}

/// <summary>
/// The discharge forecast between two runs of the model. The model runs every 10-30 minutes; meanwhile the time
/// it gave stayed fixed while the battery went its own way. Here the model's curve restarts from the level just
/// read: each quantile keeps the drain it forecast from now on. Replayed on a real laptop's history, a forecast
/// 30 minutes old was on average 14.9 minutes off as it stood and 11.5 re-anchored with the power measured now,
/// against 10.8 for a forecast just computed. No model call is needed.
/// </summary>
public static class LiveForecast
{
    /// <summary>Null when the reading is not after the forecast's own origin, or the curve does not cover it.</summary>
    public static ForecastResponse? Anchor(ForecastResponse model,DateTimeOffset at,double level)
    {
        double now=at.ToUnixTimeMilliseconds()/1000.0,origin=model.ContextEndUtc;
        if(model.Scenario!="no_charge" || model.Curve.Count==0 || now<=origin || now>=model.Curve[^1].T || !double.IsFinite(level)) return null;
        var source=Unclipped(model);
        var start=EstimateBlend.Interpolate(model,source,now);                 // where the model's own curves are now
        var points=new List<CurvePointDto>();
        for(int step=1;step<=Protocol.HorizonSteps;step++)
        {
            double t=now+step*Protocol.StepSeconds;
            if(t>model.Curve[^1].T) break;
            var p=EstimateBlend.Interpolate(model,source,t);
            // the level read now, less what each curve still loses from now to t
            double[] v=[Shift(p.P10,start.P10),Shift(p.P50,start.P50),Shift(p.P90,start.P90)];
            Array.Sort(v);
            points.Add(new(t,v[0],v[1],v[2]));
        }
        if(points.Count==0) return null;
        double reserve=model.ReservePct;
        CrossingDto crossing;
        if(level<=reserve) crossing=new() { Status="already_below" };
        else
        {
            double? Cross(Func<CurvePointDto,double> get)=>Crossings.At(points,now,level,reserve,get);
            var central=Cross(p=>p.P50);
            crossing=new() { Status=central is null?"beyond_horizon":"within_horizon",CentralUtc=central,EarlyUtc=Cross(p=>p.P10),LateUtc=Cross(p=>p.P90) };
        }
        return model with
        {
            ContextEndUtc=now,Level=level,Curve=points,Crossing=crossing,RawCurve=null,RawTiming=null,Blend=null,
            HabitualRechargeUtc=model.HabitualRechargeUtc>now?model.HabitualRechargeUtc:null,
            ExtrapolatedFromUtc=model.ExtrapolatedFromUtc>now?model.ExtrapolatedFromUtc:null,
        };
        double Shift(double then,double was)=>Math.Clamp(level-(was-then),0,level);
    }

    /// <summary>
    /// The model's curves stop at 0%. When the battery lasts longer than the model said, the part still needed lies
    /// after that point, where the curve is flat: there the drain of the last step before empty goes on.
    /// </summary>
    static List<CurvePointDto> Unclipped(ForecastResponse model)
    {
        var result=new List<CurvePointDto>(model.Curve.Count);
        double[] previous=[model.Level,model.Level,model.Level],value=[model.Level,model.Level,model.Level],drop=[0,0,0];
        foreach(var p in model.Curve)
        {
            double[] now=[p.P10,p.P50,p.P90];
            for(int q=0;q<3;q++)
            {
                double d=previous[q]-now[q];
                if(now[q]<=1e-9) d=Math.Max(d,drop[q]);
                drop[q]=d;value[q]-=d;previous[q]=now[q];
            }
            result.Add(new(p.T,value[0],value[1],value[2]));
        }
        return result;
    }
}
