using System.Runtime.InteropServices;
using WinBattery = Windows.Devices.Power.Battery;

namespace BatteryForecast.Core.Battery;

public interface IBatteryProvider
{
    BatterySample Read();
}

/// <summary>
/// GetSystemPowerStatus (percentuale e alimentazione, la stessa fonte dell'icona di sistema) più il
/// report aggregato WinRT (capacità e potenza, se il firmware le espone). Con più batterie Windows
/// aggrega già le capacità: non si fa la media delle percentuali.
/// </summary>
public sealed class WindowsBatteryProvider(TimeProvider? clock = null) : IBatteryProvider
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public BatterySample Read()
    {
        var now = clock.GetUtcNow();
        if (!GetSystemPowerStatus(out var s))
            return new BatterySample(now, ChargeState.Unknown, null, null, Source: "windows-error");
        var raw = new SystemPowerStatusRaw(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent,
            s.SystemStatusFlag, s.BatteryLifeTime, s.BatteryFullLifeTime);
        return PowerStatusDecoder.Decode(now, raw, ReadReport());
    }

    static BatteryReportRaw? ReadReport()
    {
        try
        {
            var r = WinBattery.AggregateBattery.GetReport();
            return new BatteryReportRaw(r.Status.ToString(), r.ChargeRateInMilliwatts,
                r.RemainingCapacityInMilliwattHours, r.FullChargeCapacityInMilliwattHours,
                r.DesignCapacityInMilliwattHours);
        }
        catch (Exception)      // API non disponibile o driver che rifiuta la richiesta: resta la lettura semplice
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
