using System.Text.Json;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Storage;

public sealed record AppPowerAssociation(string Name,int Windows,double? MedianPcWatts);

public sealed partial class HistoryStore
{
    public IReadOnlyList<AppPowerAssociation> ProcessAssociations(SystemSample current,DateTimeOffset since,DateTimeOffset until)
    {
        if(current.ProcessLoads is not { Count:>0 } || current.CpuPct is not { } total || current.WorkloadProfile is not { } profile) return [];
        lock(gate)
        {
            var data=ModelCovariates(since,until,currentBatteryOnly:true,includeApps:true);
            var watts=data["discharge_w"].ToDictionary(p=>p.Ts,p=>p.Value);
            var cpu=data["cpu_pct"].ToDictionary(p=>p.Ts,p=>p.Value);
            var profiles=data["workload_profile"].ToDictionary(p=>p.Ts,p=>p.Value);
            var display=data["display_state"].ToDictionary(p=>p.Ts,p=>p.Value);
            var brightness=data["brightness_pct"].ToDictionary(p=>p.Ts,p=>p.Value);
            var breaks=AggregationBarriers(since,until);
            int Band(double load)=>load<5?0:load<20?1:load<50?2:3;
            var result=new List<AppPowerAssociation>();
            foreach(var app in current.ProcessLoads.Take(6))
            {
                var matched=new List<double>();
                if(app.CpuPct is { } load && data.TryGetValue(ProcessFeatures.Cpu(app.Name),out var history))
                    foreach(var point in history)
                    {
                        var power=watts.GetValueOrDefault(point.Ts);
                        if(point.Value is not { } past || Band(past)!=Band(load) || power is not >0 ||
                            cpu.GetValueOrDefault(point.Ts) is not { } pastTotal || profiles.GetValueOrDefault(point.Ts)!=profile) continue;
                        if(Math.Abs(Math.Max(0,pastTotal-past)-Math.Max(0,total-load))>10) continue;
                        if(current.DisplayState is { } state && display.GetValueOrDefault(point.Ts)!=state) continue;
                        if(current.BrightnessPct is { } light && (brightness.GetValueOrDefault(point.Ts) is not { } oldLight || Math.Abs(oldLight-light)>10)) continue;
                        var edge=TemporalBins.UpperBound(breaks,point.Ts-0.001);
                        if(edge<breaks.Count && breaks[edge]<point.Ts+300) continue;
                        matched.Add(power.Value);
                    }
                matched.Sort();
                double? median=matched.Count>=6 ? matched.Count%2==1 ? matched[matched.Count/2] : (matched[matched.Count/2-1]+matched[matched.Count/2])/2 : null;
                result.Add(new(app.Name,matched.Count,median));
            }
            return result;
        }
    }

    Dictionary<string,IReadOnlyList<ModelCovariate>> AppCovariates(DateTimeOffset since,DateTimeOffset until,bool currentBatteryOnly)
    {
        using var cmd=db.CreateCommand();
        const string query="""
            SELECT COALESCE(measured_at,ts),process_start,processes_json FROM context_samples
            WHERE ts >= $since AND COALESCE(measured_at,ts) <= $until AND processes_json IS NOT NULL
            AND ($current=0 OR ts >= COALESCE((SELECT MIN(ts) FROM battery_samples WHERE battery_epoch>0
                AND battery_epoch=(SELECT battery_epoch FROM battery_samples WHERE ts <= $until ORDER BY ts DESC LIMIT 1)),$since))
            """;
        cmd.Parameters.AddWithValue("$since",since.ToUnixTimeSeconds());cmd.Parameters.AddWithValue("$until",Seconds(until));
        cmd.Parameters.AddWithValue("$current",currentBatteryOnly?1:0);
        ProcessLoad[]? Decode(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<ProcessLoad[]>(json)?.Where(p=>p is not null && !string.IsNullOrWhiteSpace(p.Name)).ToArray();
            }
            catch(JsonException) { return null; }
        }
        var output=new Dictionary<string,IReadOnlyList<ModelCovariate>>();
        string[] selected;
        cmd.CommandText=query+" ORDER BY ts DESC LIMIT 1";
        using(var latest=cmd.ExecuteReader())
        {
            if(!latest.Read() || Seconds(until)-latest.GetDouble(0)>150 || Decode(latest.GetString(2)) is not { Length:>0 } loads) return output;
            selected=loads.OrderByDescending(p=>p.CpuPct??-1).ThenByDescending(p=>p.WorkingSetMB)
                .Select(p=>ProcessFeatures.Normalize(p.Name)).Distinct(StringComparer.Ordinal).Take(6).ToArray();
        }
        var barriers=AggregationBarriers(since,until);
        var bins=selected.ToDictionary(name=>name,_=>new TemporalBins(2,[],Seconds(since),Seconds(until),barriers));
        void Add((double Time,double? Start,ProcessLoad[] Loads) row,double end)
        {
            foreach(var name in selected)
            {
                var app=row.Loads.FirstOrDefault(p=>ProcessFeatures.Normalize(p.Name)==name);
                // Outside the retained top groups means unknown, never an invented zero CPU observation.
                bins[name].Point(row.Time,end,[app?.CpuPct,app?.WorkingSetMB],intervalChannel:0);
                if(row.Start is { } start) bins[name].Interval(0,start,row.Time,app?.CpuPct);
            }
        }
        cmd.CommandText=query+" ORDER BY ts";
        using(var reader=cmd.ExecuteReader())
        {
            (double Time,double? Start,ProcessLoad[] Loads)? previous=null;
            while(reader.Read())
            {
                var loads=Decode(reader.GetString(2))??[];
                var current=(Time:reader.GetDouble(0),Start:NullD(reader,1),Loads:loads);
                if(previous is { } row) Add(row,current.Time);
                previous=current;
            }
            if(previous is { } last) Add(last,Seconds(until));
        }
        foreach(var name in selected)
        {
            var values=bins[name].Rows().ToArray();
            output[ProcessFeatures.Cpu(name)]=values.Select(r=>new ModelCovariate(r.Ts,r.Values[0])).ToArray();
            output[ProcessFeatures.Memory(name)]=values.Select(r=>new ModelCovariate(r.Ts,r.Values[1])).ToArray();
        }
        return output;
    }
}
