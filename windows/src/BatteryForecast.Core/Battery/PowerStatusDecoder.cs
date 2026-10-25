namespace BatteryForecast.Core.Battery;

/// <summary>Campi grezzi di SYSTEM_POWER_STATUS (winbase.h).</summary>
public readonly record struct SystemPowerStatusRaw(
    byte ACLineStatus, byte BatteryFlag, byte BatteryLifePercent, byte SystemStatusFlag,
    uint BatteryLifeTime, uint BatteryFullLifeTime);

/// <summary>Campi grezzi di Windows.Devices.Power.BatteryReport (aggregato di tutte le batterie).</summary>
public readonly record struct BatteryReportRaw(
    string Status, int? ChargeRateMW, int? RemainingMWh, int? FullChargeMWh, int? DesignMWh);

/// <summary>
/// Traduce le letture delle API Windows in <see cref="BatterySample"/>. Funzione pura: si prova senza batteria.
/// </summary>
public static class PowerStatusDecoder
{
    const byte Unknown = 255;
    const byte FlagCharging = 8, FlagNoBattery = 128;
    const uint UnknownSeconds = 0xFFFFFFFF;     // BatteryLifeTime -1 come DWORD

    public static BatterySample Decode(DateTimeOffset now, SystemPowerStatusRaw s, BatteryReportRaw? report)
    {
        bool? onAc = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        double? level = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;

        var state = ChargeState.Unknown;
        if (s.BatteryFlag != Unknown && (s.BatteryFlag & FlagNoBattery) != 0)
            state = ChargeState.NotPresent;
        else if (report is { } r)
            state = r.Status switch
            {
                "NotPresent" => ChargeState.NotPresent,
                "Discharging" => ChargeState.Discharging,
                "Idle" => ChargeState.Idle,
                "Charging" => ChargeState.Charging,
                _ => ChargeState.Unknown,
            };
        if (state == ChargeState.Unknown && s.BatteryFlag != Unknown && level is not null)
            state = (s.BatteryFlag & FlagCharging) != 0 ? ChargeState.Charging
                  : onAc == true ? ChargeState.Idle : ChargeState.Discharging;

        if (state == ChargeState.NotPresent)
            return new BatterySample(now, state, null, onAc, Source: "windows");

        int? remaining = Positive(report?.RemainingMWh), full = Positive(report?.FullChargeMWh),
             design = Positive(report?.DesignMWh);
        // L'API dà mW negativi in scarica; qui il consumo è positivo. In carica non c'è consumo da batteria.
        int? discharge = report?.ChargeRateMW is int rate && onAc != true && rate < 0 ? -rate : null;
        int? osSeconds = s.BatteryLifeTime != UnknownSeconds && onAc == false ? (int)Math.Min(s.BatteryLifeTime, int.MaxValue) : null;

        return new BatterySample(now, state, level, onAc, remaining, full, design, discharge, osSeconds,
            BatterySaver: s.SystemStatusFlag == 1, Source: "windows")
        { ChargingPowerMW = onAc == true && report?.ChargeRateMW is > 0 ? report.Value.ChargeRateMW : null };
    }

    static int? Positive(int? v) => v is > 0 ? v : null;
}
