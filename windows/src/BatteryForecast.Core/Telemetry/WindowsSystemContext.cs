using System.Collections;
using System.Runtime.InteropServices;
using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Telemetry;

/// <summary>Local counters and explicit opt-in app workloads. No window titles, paths, arguments or network traffic.</summary>
public sealed class WindowsSystemContext : ISystemContextProvider
{
    readonly TimeProvider clock;
    readonly Func<bool> collectAdvanced;
    readonly Func<double?> displayState;
    readonly Func<bool> collectProcesses;
    readonly ProcessLoadSampler processes=new(new WindowsProcessCounterSource(),Environment.ProcessorCount);
    ulong? previousIdle, previousTotal;
    DateTimeOffset? previousCpu, optionalAt;
    int optionalBusy;
    readonly Lock optionalGate = new();
    OptionalSensors optional = new();
    public IReadOnlyDictionary<string,string> OptionalSensorErrors { get { lock(optionalGate) return optional.Errors; } }

    public WindowsSystemContext(TimeProvider? clock = null, Func<bool>? collectAdvanced = null, Func<double?>? displayState = null,Func<bool>? collectProcesses = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.collectAdvanced = collectAdvanced ?? (() => true);
        this.displayState = displayState ?? (() => null);
        this.collectProcesses=collectProcesses ?? (()=>false);
    }

    public SystemSample Read(BatterySample battery)
    {
        var now = clock.GetUtcNow();
        double? cpu = null;
        DateTimeOffset? cpuStart = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel+user;
            if (previousIdle is { } pi && previousTotal is { } pt && previousCpu is { } at &&
                now-at >= TimeSpan.FromSeconds(1) && now-at <= TimeSpan.FromMinutes(10))
            {
                cpu = SensorMath.CpuPercent(pi,pt,idle,total);
                if(cpu is not null) cpuStart=at;
            }
            if (previousCpu is null || now-previousCpu >= TimeSpan.FromSeconds(1))
            { previousIdle=idle; previousTotal=total; previousCpu=now; }
        }
        var memory = new MemoryStatus { Length=(uint)Marshal.SizeOf<MemoryStatus>() };
        double? memoryPct = GlobalMemoryStatusEx(ref memory) && memory.MemoryLoad<=100 ? memory.MemoryLoad : null;
        var processors = new ProcessorPower[Math.Max(1,Environment.ProcessorCount)];
        double? mhz = null;
        if (CallNtPowerInformation(11,IntPtr.Zero,0,processors,(uint)(processors.Length*Marshal.SizeOf<ProcessorPower>())) == 0)
        {
            var values = processors.Where(p => p.CurrentMHz>0 && p.CurrentMHz<20000).Select(p => (double)p.CurrentMHz).ToArray();
            if (values.Length>0) mhz=values.Average();
        }
        double? profile = null;
        if (PowerGetActiveScheme(IntPtr.Zero,out var guidPtr)==0)
        {
            try
            {
                var guid=Marshal.PtrToStructure<Guid>(guidPtr);
                profile=guid==new Guid("381b4222-f694-41f0-9685-ff5bb260df2e") ? 0 :
                    guid==new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") ? 1 :
                    guid==new Guid("a1841308-3541-4fab-bc81-f71556f20b4a") ? 2 : 3;
            }
            finally { LocalFree(guidPtr); }
        }
        OptionalSensors sensors;
        DateTimeOffset? sensorAt=null;
        lock(optionalGate)
        {
            if (!collectAdvanced())
            {
                optional = new();
                optionalAt = null;
                sensors = new();
            }
            else
            {
                sensors = optionalAt is { } at && now-at <= TimeSpan.FromMinutes(2) ? optional : new();
                if(optionalAt is { } measured && now-measured<=TimeSpan.FromMinutes(2)) sensorAt=measured;
                if ((optionalAt is null || now-optionalAt >= TimeSpan.FromSeconds(50)) && Interlocked.CompareExchange(ref optionalBusy,1,0)==0)
                {
                    // One background request maximum, even if a firmware/WMI provider is slow.
                    _=Task.Run(() =>
                    {
                        try
                        {
                            var fresh=OptionalSensors.Read(Query, BatteryTemperatureProbe.ReadMaximum);
                            lock(optionalGate) { if (collectAdvanced()) { optional=fresh; optionalAt=this.clock.GetUtcNow(); } }
                        }
                        catch(Exception e) when (e is not OutOfMemoryException)
                        {
                            lock(optionalGate) { optional=new(Errors: new Dictionary<string,string> { ["probe"]=e.GetType().Name }); optionalAt=this.clock.GetUtcNow(); }
                        }
                        finally { Interlocked.Exchange(ref optionalBusy,0); }
                    });
                }
            }
        }
        ProcessSnapshot? workload=null;
        if(collectProcesses())
        {
            try { workload=processes.Read(now); }
            catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { processes.Reset(); }
        }
        else processes.Reset();
        if(!collectProcesses()) { processes.Reset();workload=null; }
        return new(now,cpu,mhz,memoryPct,sensors.CpuC,sensors.BatteryC,sensors.ZoneC,sensors.Brightness,
            battery.DischargePowerMW/1000.0,battery.ChargingPowerMW/1000.0,profile, DisplayState: displayState(),
            CpuIntervalStartUtc:cpuStart,OptionalObservedUtc:sensorAt,ProcessLoads:workload?.Loads,ProcessCpuPct:workload?.CpuPct,
            ProcessMemoryMB:workload?.WorkingSetMB,WorkloadProfile:workload?.Profile,ProcessIntervalStartUtc:workload?.IntervalStart);
    }

    public void ResetIntervals() { previousIdle=null; previousTotal=null; previousCpu=null;processes.Reset(); }

    internal static double[] Query(string ns,string query,string property)
    {
        object? locator=null,service=null,result=null;
        try
        {
            var type=Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if(type is null)return [];
            locator=Activator.CreateInstance(type);
            // Missing variants preserve COM defaults. Null is not an omitted optional argument.
            service=((dynamic)locator!).ConnectServer(".",ns,Type.Missing,Type.Missing,Type.Missing,Type.Missing,128,Type.Missing);
            // .NET's IEnumerable adapter rewinds the enumerator; forward-only results cannot do that.
            result=((dynamic)service!).ExecQuery(query,"WQL",16);
            var values=new List<double>();
            foreach(object row in (IEnumerable)result!)
            {
                object? properties=null,item=null;
                try
                {
                    properties=((dynamic)row).Properties_;
                    item=((dynamic)properties).Item(property);
                    object? raw=((dynamic)item).Value;
                    if(raw is null || raw is DBNull) continue;
                    var v=Convert.ToDouble(raw,System.Globalization.CultureInfo.InvariantCulture);
                    if(double.IsFinite(v))values.Add(v);
                }
                finally { Release(item); Release(properties); Release(row); }
            }
            return [..values];
        }
        finally { Release(result); Release(service); Release(locator); }
    }
    static void Release(object? value) { if(value is not null && Marshal.IsComObject(value))Marshal.ReleaseComObject(value); }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus { public uint Length,MemoryLoad; public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessorPower { public uint Number,MaxMHz,CurrentMHz,MHzLimit,MaxIdleState,CurrentIdleState; }
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("powrprof.dll")]
    static extern uint CallNtPowerInformation(int level,IntPtr input,uint inputSize,[Out] ProcessorPower[] output,uint outputSize);
    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr guid);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
