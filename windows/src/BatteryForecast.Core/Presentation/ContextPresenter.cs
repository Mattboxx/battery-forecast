using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Presentation;

/// <summary>Local sensor evidence, for Preferences only. No measurements are substituted for unavailable values.</summary>
public static class ContextPresenter
{
    public static string Present(Settings settings, SystemSample? sample, ContextHistory history, IReadOnlyList<string> used)
    {
        var lines = new List<string>
        {
            settings.CollectSystemContext ? L.F("ContextRecording", settings.ContextIntervalSeconds) : L.T("ContextOff"),
            L.F("ContextStored", history.Samples, settings.ContextHistoryHours),
            history.LastSaved is { } last ? L.F("ContextLastSaved", last.ToLocalTime().ToString("g", L.Culture)) : L.T("ContextNeverSaved"),
        };
        if (sample is not null) lines.Add(L.F("ContextLastReading", sample.TimestampUtc.ToLocalTime().ToString("g", L.Culture)));
        var values = new (string Key, double? Value, string Unit)[]
        {
            ("cpu_pct", sample?.CpuPct, "%"), ("cpu_mhz", sample?.CpuMHz, "MHz"),
            ("memory_pct", sample?.MemoryPct, "%"), ("discharge_w", sample?.DischargeW, "W"),
            ("charge_w", sample?.ChargeW, "W"), ("cpu_temp_c", sample?.CpuTemperatureC, "°C"),
            ("battery_temp_c", sample?.BatteryTemperatureC, "°C"), ("thermal_zone_c", sample?.ThermalZoneC, "°C"),
            ("brightness_pct", sample?.BrightnessPct, "%"), ("power_profile", sample?.PowerProfile, ""),
            ("display_state", sample?.DisplayState, ""),
            ("process_cpu_pct",sample?.ProcessCpuPct,"%"),("process_memory_mb",sample?.ProcessMemoryMB,"MB"),
        };
        foreach (var (key, value, unit) in values)
        {
            var text = value is double v && double.IsFinite(v)
                ? key == "display_state" ? L.T("DisplayState_" + (int)v) : key == "power_profile" ? L.T("Profile_" + (int)v) : v.ToString("0.#", L.Culture) + " " + unit
                : L.T("SensorUnavailableValue");
            lines.Add(L.F("ContextChannel", L.T("Sensor_" + key), text, history.Observations.GetValueOrDefault(key)));
        }
        lines.Add(!settings.UseSystemContext || !settings.CollectSystemContext ? L.T("ContextModelOff")
            : used.Count > 0 ? L.F("ContextLastUsed", string.Join(", ", used.Select(k => SensorLabel(k,sample))))
            : L.T("ContextNotYetUsed"));
        lines.Add(settings.CollectProcesses && settings.CollectSystemContext ? L.T("ProcessesOn") : L.T("ProcessesOff"));
        if(settings.CollectProcesses && settings.CollectSystemContext && sample?.ProcessLoads is { Count:>0 } processes)
            foreach(var process in processes)
                lines.Add(L.F("ProcessReading",process.Name,process.CpuPct?.ToString("0.#",L.Culture)??"—",process.WorkingSetMB.ToString("0.#",L.Culture)));
        return string.Join(Environment.NewLine, lines);
    }

    static string SensorLabel(string key,SystemSample? sample)
    {
        if(!key.StartsWith("app_",StringComparison.Ordinal)) return L.T("Sensor_"+key);
        foreach(var app in sample?.ProcessLoads??[])
        {
            if(key==ProcessFeatures.Cpu(app.Name)) return L.F("SensorAppCpu",app.Name);
            if(key==ProcessFeatures.Memory(app.Name)) return L.F("SensorAppMemory",app.Name);
        }
        return L.T("SensorAppPast");
    }
}
