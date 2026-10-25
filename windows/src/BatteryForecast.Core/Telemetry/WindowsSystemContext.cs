using System.Collections;
using System.Runtime.InteropServices;
using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Telemetry;

/// <summary>Local aggregate counters only. No processes, titles, identifiers or network traffic.</summary>
public sealed class WindowsSystemContext : ISystemContextProvider
{
    readonly TimeProvider clock;
    ulong? previousIdle, previousTotal;
    DateTimeOffset? previousCpu, optionalAt;
    int optionalBusy;
    readonly Lock optionalGate = new();
    OptionalSensors optional = new();
    sealed record OptionalSensors(double? CpuC = null, double? BatteryC = null, double? ZoneC = null, double? Brightness = null);

    public WindowsSystemContext(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public SystemSample Read(BatterySample battery)
    {
        var now = clock.GetUtcNow();
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel+user;
            if (previousIdle is { } pi && previousTotal is { } pt && previousCpu is { } at &&
                now-at >= TimeSpan.FromSeconds(1) && now-at <= TimeSpan.FromMinutes(2))
                cpu = SensorMath.CpuPercent(pi,pt,idle,total);
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
        lock(optionalGate)
        {
            sensors = optionalAt is { } at && now-at <= TimeSpan.FromMinutes(2) ? optional : new();
            if ((optionalAt is null || now-optionalAt >= TimeSpan.FromSeconds(50)) && Interlocked.CompareExchange(ref optionalBusy,1,0)==0)
            {
                // One background request maximum, even if a firmware/WMI provider is slow.
                _=Task.Run(() =>
                {
                    try
                    {
                        var fresh=ReadOptional();
                        lock(optionalGate) { optional=fresh; optionalAt=this.clock.GetUtcNow(); }
                    }
                    finally { Interlocked.Exchange(ref optionalBusy,0); }
                });
            }
        }
        return new(now,cpu,mhz,memoryPct,sensors.CpuC,sensors.BatteryC,sensors.ZoneC,sensors.Brightness,
            battery.DischargePowerMW/1000.0,battery.ChargingPowerMW/1000.0,profile);
    }

    static OptionalSensors ReadOptional()
    {
        double? cpu = null;
        foreach(var ns in new[] {@"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor"})
        {
            var values=Query(ns,"SELECT Value FROM Sensor WHERE SensorType='Temperature' AND (Name LIKE '%CPU Package%' OR Name LIKE '%CPU Core%')","Value");
            if(values.Length>0) { cpu=values.Max(); break; }
        }
        if(cpu is not (>= -30 and <= 130)) cpu=null;
        var zones=Query(@"root\WMI","SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature","CurrentTemperature")
            .Select(v => v>=0 && v<=uint.MaxValue ? SensorMath.KelvinTenthsToCelsius((uint)v) : null).Where(v => v is not null).ToArray();
        var brightness=Query(@"root\WMI","SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=True","CurrentBrightness")
            .Where(v=>v is >=0 and <=100).ToArray();
        return new(cpu,BatteryTemperatureProbe.ReadMaximum(),zones.Length>0 ? zones.Max() : null,
            brightness.Length>0 ? brightness.Average() : null);
    }

    static double[] Query(string ns,string query,string property)
    {
        object? locator=null,service=null,result=null;
        try
        {
            var type=Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if(type is null)return [];
            locator=Activator.CreateInstance(type);
            service=((dynamic)locator!).ConnectServer(".",ns,null,null,null,null,128,null);
            result=((dynamic)service!).ExecQuery(query,"WQL",48);
            var values=new List<double>();
            foreach(object row in (IEnumerable)result!)
            {
                try { var v=Convert.ToDouble(((dynamic)row).Properties_.Item(property).Value,System.Globalization.CultureInfo.InvariantCulture); if(double.IsFinite(v))values.Add(v); }
                finally { Release(row); }
            }
            return [..values];
        }
        catch(Exception e) when(e is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or FormatException)
        { return []; }
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
