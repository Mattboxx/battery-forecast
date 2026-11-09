namespace BatteryForecast.Core.Forecasting;

/// <summary>
/// Time at the charge target from the measured charging (previous charges of this PC from the same level, or the
/// slope of the current charge), with the Timestral "stay plugged in" curve retimed to it.
/// Replayed on a real laptop's history (168 moments of 18 charges that reached 80%): past charges were on average
/// 2.3 minutes off and the current slope 3, while the Timestral time was 89 minutes off and missing for more than
/// half of the moments. So the measured time leads whenever it exists; without it the Timestral forecast stays.
/// </summary>
public static class ChargeBlend
{
    /// <summary>Band of a curve drawn from the measured time alone: ±20% of the remaining minutes.</summary>
    public const double Spread=.2;

    public static ForecastResponse Apply(ForecastResponse r,ChargeEstimate measured)
    {
        if(r.Scenario!="charge" || measured.TargetAt is not { } at || r.TargetPct is not double target || r.Level>=target) return r;
        double origin=r.ContextEndUtc,minutes=(at.ToUnixTimeMilliseconds()/1000.0-origin)/60;
        if(minutes<=0) return r;
        double? timestral=r.Crossing.Status=="within_horizon" && r.Crossing.CentralUtc is { } c && c>origin ? (c-origin)/60 : null;
        var blend=new BlendInfo { PowerWeight=1,PowerMinutes=minutes,TimestralMinutes=timestral };
        var points=new List<CurvePointDto>();
        double speed=1;
        double? Cross(Func<CurvePointDto,double> get)=>points.FirstOrDefault(p=>get(p)>=target-1e-9)?.T;
        if(timestral is { } own && r.Curve.Count>0)
        {
            speed=own/minutes;                                        // >1: the measured arrival is earlier
            for(int step=1;step<=Protocol.HorizonSteps;step++)
            {
                double t=origin+step*Protocol.StepSeconds,u=origin+(t-origin)*speed;
                if(u>r.Curve[^1].T) break;
                points.Add(EstimateBlend.Interpolate(r,r.Curve,u) with { T=t });
            }
        }
        // Timestral does not reach the target, or its curve is too short once retimed (the measured arrival is
        // minutes away, Timestral's hours away): a straight line to the measured time, then the target held
        bool warped=Cross(p=>p.P50) is not null;
        if(!warped)
        {
            points.Clear();speed=1;
            double Level(double t,double scale)=>Math.Min(target,r.Level+(target-r.Level)*(t-origin)/60/(minutes*scale));
            for(int step=1;step<=Protocol.HorizonSteps;step++)
            {
                double t=origin+step*Protocol.StepSeconds;
                points.Add(new(t,Level(t,1+Spread),Level(t,1),Level(t,1-Spread)));
            }
        }
        var central=Cross(p=>p.P50);
        return r with
        {
            Curve=points,Blend=blend,RawCurve=r.RawCurve??r.Curve,RawTiming=r.RawTiming??EstimateBlend.Timing(r),
            // in charge the highest curve (p90) arrives first and the lowest (p10) last
            Crossing=r.Crossing with { Status=central is null?"beyond_horizon":"within_horizon",CentralUtc=central,EarlyUtc=Cross(p=>p.P90),LateUtc=Cross(p=>p.P10) },
            HabitualUnplugUtc=warped?Warp(r.HabitualUnplugUtc):null,ExtrapolatedFromUtc=warped?Warp(r.ExtrapolatedFromUtc):null,
            ExtrapolationRatePctPerHour=warped?r.ExtrapolationRatePctPerHour*speed:null,
        };
        double? Warp(double? t)=>t is { } v ? origin+(v-origin)/speed : null;
    }
}
