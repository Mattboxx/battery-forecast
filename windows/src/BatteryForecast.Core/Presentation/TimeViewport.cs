namespace BatteryForecast.Core.Presentation;

/// <summary>Bounded time navigation; never changes chart data or runs a forecast.</summary>
public sealed class TimeViewport(TimeSpan minimum)
{
    public DateTimeOffset From { get; private set; }
    public DateTimeOffset Until { get; private set; }
    public DateTimeOffset FullFrom { get; private set; }
    public DateTimeOffset FullUntil { get; private set; }
    public bool IsZoomed => Until-From < FullUntil-FullFrom;
    public void SetBounds(DateTimeOffset from,DateTimeOffset until,bool reset=false)
    {
        if(until<=from) throw new ArgumentException("Positive time range required");
        bool wasFull=Until<=From || !IsZoomed;
        double span=(Until-From).TotalSeconds;
        var start=From;FullFrom=from;FullUntil=until;
        if(reset || wasFull) Reset(); else Set(start,span);
    }
    public void Reset() { From=FullFrom;Until=FullUntil; }
    public void Zoom(double factor,double anchor=.5)
    {
        if(!double.IsFinite(factor)||factor<=0||!double.IsFinite(anchor)||Until<=From) return;
        anchor=Math.Clamp(anchor,0,1);
        double old=(Until-From).TotalSeconds;
        double span=Math.Clamp(old/factor,Math.Min(minimum.TotalSeconds,(FullUntil-FullFrom).TotalSeconds),(FullUntil-FullFrom).TotalSeconds);
        Set(From.AddSeconds((old-span)*anchor),span);
    }
    public void Pan(double fraction)
    {
        if(double.IsFinite(fraction)) Set(From.AddSeconds((Until-From).TotalSeconds*Math.Clamp(fraction,-2,2)),(Until-From).TotalSeconds);
    }
    void Set(DateTimeOffset from,double span)
    {
        span=Math.Clamp(span,0,(FullUntil-FullFrom).TotalSeconds);
        var latest=FullUntil.AddSeconds(-span);
        From=from<FullFrom?FullFrom:from>latest?latest:from;Until=From.AddSeconds(span);
    }
}
