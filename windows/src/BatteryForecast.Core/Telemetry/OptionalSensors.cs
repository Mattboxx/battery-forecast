namespace BatteryForecast.Core.Telemetry;

internal sealed record OptionalSensors(double? CpuC = null, double? BatteryC = null, double? ZoneC = null,
    double? Brightness = null, IReadOnlyDictionary<string,string>? Errors = null)
{
    public IReadOnlyDictionary<string,string> Errors { get; init; } = Errors ?? new Dictionary<string,string>();

    internal static OptionalSensors Read(Func<string,string,string,double[]> query, Func<double?> battery)
    {
        var errors=new Dictionary<string,string>();
        double[] Read(string key,string ns,string wql,string property)
        {
            try { return query(ns,wql,property).Where(double.IsFinite).ToArray(); }
            catch(Exception e) when(e is not OutOfMemoryException)
            { errors[key]=$"{e.GetType().Name} (0x{e.HResult:X8})"; return []; }
        }
        double? cpu=null;
        foreach(var ns in new[] {@"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor"})
        {
            var values=Read(ns,ns,"SELECT Value FROM Sensor WHERE SensorType='Temperature' AND (Name LIKE '%CPU Package%' OR Name LIKE '%CPU Core%')","Value")
                .Where(v=>v is >= -30 and <=130).ToArray();
            if(values.Length>0) { cpu=values.Max(); break; }
        }
        var zones=Read("thermal_zone_c",@"root\WMI","SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature","CurrentTemperature")
            .Select(v=>v>=0 && v<=uint.MaxValue ? SensorMath.KelvinTenthsToCelsius((uint)v) : null).Where(v=>v is not null).ToArray();
        var brightness=Read("brightness_pct",@"root\WMI","SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=True","CurrentBrightness")
            .Where(v=>v is >=0 and <=100).ToArray();
        double? batteryC=null;
        try { var value=battery(); if(value is >= -30 and <=130) batteryC=value; }
        catch(Exception e) when(e is not OutOfMemoryException) { errors["battery_temp_c"]=e.GetType().Name; }
        return new(cpu,batteryC,zones.Length>0?zones.Max():null,brightness.Length>0?brightness.Average():null,errors);
    }
}
