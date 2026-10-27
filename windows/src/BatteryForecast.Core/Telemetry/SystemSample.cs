using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Telemetry;

public sealed record SystemSample(DateTimeOffset TimestampUtc, double? CpuPct = null, double? CpuMHz = null,
    double? MemoryPct = null, double? CpuTemperatureC = null, double? BatteryTemperatureC = null,
    double? ThermalZoneC = null, double? BrightnessPct = null, double? DischargeW = null,
    double? ChargeW = null, double? PowerProfile = null, string Source = "windows-api", double? DisplayState = null,
    DateTimeOffset? CpuIntervalStartUtc = null, string CollectionKind = "unspecified", DateTimeOffset? OptionalObservedUtc = null,
    IReadOnlyList<ProcessLoad>? ProcessLoads = null, double? ProcessCpuPct = null,double? ProcessMemoryMB = null,
    long? WorkloadProfile = null,DateTimeOffset? ProcessIntervalStartUtc = null);

public sealed record ContextHistory(long Samples, DateTimeOffset? LastSaved, IReadOnlyDictionary<string, long> Observations);

public interface ISystemContextProvider
{
    SystemSample Read(BatterySample battery);
    void ResetIntervals() { }
}

public static class SensorMath
{
    public static double? CpuPercent(ulong idleBefore, ulong totalBefore, ulong idleNow, ulong totalNow)
    {
        if (totalNow <= totalBefore || idleNow < idleBefore) return null;
        var total = totalNow-totalBefore; var idle = idleNow-idleBefore;
        return idle <= total ? 100.0*(total-idle)/total : null;
    }
    public static double? KelvinTenthsToCelsius(uint value)
    {
        var c = value/10.0-273.15;
        return c is >= -30 and <= 130 ? Math.Round(c,1) : null;
    }
}
