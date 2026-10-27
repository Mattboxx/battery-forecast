using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BatteryForecast.Core.Telemetry;

public sealed record ProcessLoad(string Name,double? CpuPct,double WorkingSetMB);
public static class ProcessFeatures
{
    public static string Normalize(string name)
    {
        var value=name.Trim().ToLowerInvariant();return value[..Math.Min(100,value.Length)];
    }
    public static string Id(string name)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(name))))[..12].ToLowerInvariant();
    public static string Cpu(string name)=>"app_"+Id(name)+"_cpu";
    public static string Memory(string name)=>"app_"+Id(name)+"_memory";
}
internal sealed record ProcessCounter(int Pid,long Started,string Name,TimeSpan Cpu,double WorkingSetMB);
internal interface IProcessCounterSource { IReadOnlyList<ProcessCounter> Read(); }

internal sealed class WindowsProcessCounterSource : IProcessCounterSource
{
    public IReadOnlyList<ProcessCounter> Read()
    {
        var rows=new List<ProcessCounter>();int scanned=0;
        foreach(var process in Process.GetProcesses())
        using(process)
        {
            if(scanned++>=512) continue;
            try
            {
                // ProcessName is an executable name, never a path, command line or window title.
                var name=process.ProcessName;
                if(process.Id==Environment.ProcessId || name.Equals("battery_forecast",StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new(process.Id,process.StartTime.ToUniversalTime().Ticks,name,
                    process.TotalProcessorTime,process.WorkingSet64/(1024.0*1024)));
            }
            catch(Exception e) when(e is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
        }
        return rows;
    }
}

internal sealed record ProcessSnapshot(IReadOnlyList<ProcessLoad> Loads,double? CpuPct,double? WorkingSetMB,
    long? Profile,DateTimeOffset? IntervalStart);

internal sealed class ProcessLoadSampler(IProcessCounterSource source,int processors)
{
    Dictionary<(int Pid,long Started),ProcessCounter> previous=[];
    DateTimeOffset? last;
    internal void Reset() { previous.Clear();last=null; }
    internal ProcessSnapshot? Read(DateTimeOffset now)
    {
        if(last is { } recent && now-recent<TimeSpan.FromSeconds(1)) return null;
        var current=source.Read().Take(512).Where(p=>!string.IsNullOrWhiteSpace(p.Name)&&p.WorkingSetMB>=0&&double.IsFinite(p.WorkingSetMB))
            .GroupBy(p=>(p.Pid,p.Started)).Select(g=>g.Last()).ToArray();
        double seconds=last is { } at ? (now-at).TotalSeconds : 0;
        bool valid=seconds>=1&&seconds<=150;
        var rows=current.Select(p=>
        {
            double? cpu=null;
            if(valid && previous.TryGetValue((p.Pid,p.Started),out var old) && old.Name==p.Name && p.Cpu>=old.Cpu)
                cpu=Math.Clamp((p.Cpu-old.Cpu).TotalSeconds/seconds/Math.Max(1,processors)*100,0,100);
            return new ProcessLoad(ProcessFeatures.Normalize(p.Name),cpu,p.WorkingSetMB);
        }).GroupBy(p=>p.Name,StringComparer.Ordinal).Select(g=>new ProcessLoad(g.Key,
            g.Any(p=>p.CpuPct is not null)?Math.Min(100,g.Sum(p=>p.CpuPct??0)):null,g.Sum(p=>p.WorkingSetMB)))
            .OrderByDescending(p=>p.CpuPct??-1).ThenByDescending(p=>p.WorkingSetMB).ThenBy(p=>p.Name,StringComparer.Ordinal).ToArray();
        var busy=rows.Where(p=>p.CpuPct>.25).Take(3).ToArray();
        if(busy.Length==0) busy=rows.OrderByDescending(p=>p.WorkingSetMB).Take(3).ToArray();
        long? profile=null;
        if(busy.Length>0)
        {
            var key=string.Join('\n',busy.Select(p=>p.Name).Order(StringComparer.Ordinal));
            var hash=SHA256.HashData(Encoding.UTF8.GetBytes(key));long id=0;
            for(int i=0;i<6;i++) id=(id<<8)|hash[i]; // 48 bits remain exact in the model's float64 categories
            profile=id;
        }
        var result=new ProcessSnapshot(rows.Take(12).ToArray(),rows.Any(p=>p.CpuPct is not null)?Math.Min(100,rows.Sum(p=>p.CpuPct??0)):null,
            rows.Length>0?rows.Sum(p=>p.WorkingSetMB):null,profile,valid?last:null);
        previous=current.ToDictionary(p=>(p.Pid,p.Started));last=now;
        return result;
    }
}
