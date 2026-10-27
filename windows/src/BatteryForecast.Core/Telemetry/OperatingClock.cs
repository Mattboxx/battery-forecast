using System.Runtime.InteropServices;

namespace BatteryForecast.Core.Telemetry;

public readonly record struct OperatingClockReading(double ElapsedSeconds,double AwakeSeconds);
public readonly record struct SamplingBreak(DateTimeOffset From,DateTimeOffset Until,string Kind);

/// <summary>Passive OS clocks: elapsed includes sleep/hibernate, unbiased time excludes both.</summary>
internal static class OperatingClock
{
    internal static OperatingClockReading? Read()
    {
        if(!OperatingSystem.IsWindows()) return null;
        // Tick precision is sufficient for a multi-second continuity threshold and avoids timer changes.
        if(!QueryUnbiasedInterruptTime(out var awake)) return null;
        return new(GetTickCount64()/1000.0,awake/10000000.0);
    }
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [DllImport("kernel32.dll",ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool QueryUnbiasedInterruptTime(out ulong value);
}

internal sealed class SamplingContinuity
{
    DateTimeOffset? last;
    OperatingClockReading? previous;
    internal void Reset() { last=null; previous=null; }
    internal SamplingBreak? Observe(DateTimeOffset now,OperatingClockReading? reading)
    {
        var before=last; var prior=previous;last=now;previous=reading;
        if(before is not { } at) return null;
        double wall=(now-at).TotalSeconds;
        double elapsed=reading is { } r && prior is { } p ? r.ElapsedSeconds-p.ElapsedSeconds : wall;
        string? kind=null;
        if(wall<0 || elapsed<0 || (reading is not null && prior is not null && Math.Abs(wall-elapsed)>5)) kind="clock_changed";
        else if(reading is { } current && prior is { } old && elapsed-(current.AwakeSeconds-old.AwakeSeconds)>5) kind="resume_detected";
        else if(elapsed>180) kind="sampling_gap";
        return kind is null ? null : new(at,now,kind);
    }
}
