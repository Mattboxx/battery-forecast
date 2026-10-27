namespace BatteryForecast.Core.Storage;

/// <summary>Time integrals, not row-count means. Instantaneous values have bounded zero-order support.</summary>
internal sealed class TemporalBins(int channels, int[] categories, double since, double until, IReadOnlyList<double> barriers)
{
    internal const double MaxHoldSeconds = 150; // covers the selectable 120s cadence plus jitter
    readonly SortedDictionary<long,Bin> bins = [];
    readonly HashSet<int> categorical = [..categories];
    internal sealed class Bin(int channels)
    {
        internal readonly double[] Sum = new double[channels], Weight = new double[channels];
        internal readonly double?[] Point = new double?[channels];
        internal readonly bool[] Seen = new bool[channels], Mixed = new bool[channels];
        internal double? Value(int c) => Mixed[c] ? null : Weight[c]>0 ? Sum[c]/Weight[c] : Point[c];
    }
    Bin Get(double t)
    {
        long key=(long)Math.Floor(t/300)*300;
        if(!bins.TryGetValue(key,out var bin)) bins[key]=bin=new(channels);
        return bin;
    }
    void Category(Bin bin,int c,double? value)
    {
        if(value is null || (bin.Seen[c] && bin.Point[c]!=value)) bin.Mixed[c]=true;
        bin.Seen[c]=true; bin.Point[c]=value;
    }
    internal void Point(double t,double end,double?[] values,int intervalChannel=-1)
    {
        if(t<since || t>until) return;
        var bin=Get(t);
        for(int c=0;c<channels;c++)
        {
            if(categorical.Contains(c)) Category(bin,c,values[c]);
            else if(c!=intervalChannel) bin.Point[c]=values[c];
        }
        end=Math.Min(until,Math.Min(end,t+MaxHoldSeconds));
        // An instant at a barrier belongs to the new phase; never carry an older reading across it.
        int index=UpperBound(barriers,t);
        if(index<barriers.Count) end=Math.Min(end,barriers[index]);
        for(int c=0;c<channels;c++)
            if(c!=intervalChannel) Integrate(c,t,end,values[c]);
    }
    internal void Interval(int channel,double start,double end,double? value)
    {
        if(!double.IsFinite(start) || start>=end || end-start>MaxHoldSeconds || end>until) return;
        int index=UpperBound(barriers,start);
        // A CPU mean spanning a break cannot be split into known means on either side.
        if(index<barriers.Count && barriers[index]<=end) return;
        Integrate(channel,Math.Max(since,start),end,value);
    }
    void Integrate(int c,double start,double end,double? value)
    {
        if(value is not double v || !double.IsFinite(v)) return;
        while(start<end)
        {
            var bin=Get(start);
            var next=Math.Min(end,(Math.Floor(start/300)+1)*300);
            if(categorical.Contains(c)) Category(bin,c,value);
            else { bin.Sum[c]+=v*(next-start); bin.Weight[c]+=next-start; }
            start=next;
        }
    }
    internal IEnumerable<(long Ts,double?[] Values)> Rows() => bins.Select(pair =>
        (pair.Key,Enumerable.Range(0,channels).Select(pair.Value.Value).ToArray()));

    internal static int UpperBound(IReadOnlyList<double> values,double t)
    {
        int low=0,high=values.Count;
        while(low<high) { int mid=(low+high)/2; if(values[mid]<=t) low=mid+1; else high=mid; }
        return low;
    }
}
