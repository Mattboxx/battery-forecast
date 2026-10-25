using BatteryForecast.Core.Battery;

namespace BatteryForecast.Tests;

public class PowerStatusDecoderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Sentinels_become_null_not_zero()
    {
        var s = PowerStatusDecoder.Decode(Now, new(255, 255, 255, 0, 0xFFFFFFFF, 0xFFFFFFFF), null);
        Assert.Null(s.LevelPct);
        Assert.Null(s.OnAc);
        Assert.Null(s.OsRemainingSeconds);
        Assert.Equal(ChargeState.Unknown, s.State);
    }

    [Fact]
    public void No_system_battery_flag_means_not_present_even_with_controller()
    {
        var s = PowerStatusDecoder.Decode(Now, new(1, 128, 255, 0, 0xFFFFFFFF, 0xFFFFFFFF),
            new BatteryReportRaw("Idle", null, null, null, null));
        Assert.False(s.HasBattery);
        Assert.Null(s.LevelPct);
    }

    [Fact]
    public void Discharge_rate_sign_is_converted_to_positive_consumption()
    {
        var s = PowerStatusDecoder.Decode(Now, new(0, 1, 63, 0, 9000, 0xFFFFFFFF),
            new BatteryReportRaw("Discharging", -7500, 31000, 50000, 56000));
        Assert.Equal(7500, s.DischargePowerMW);
        Assert.Equal(63, s.LevelPct);
        Assert.False(s.OnAc);
        Assert.True(s.IsDischarging);
        Assert.Equal(9000, s.OsRemainingSeconds);
        Assert.Equal(100.0 * 50000 / 56000, s.HealthPct!.Value, 6);
    }

    [Fact]
    public void Charging_has_no_discharge_power_and_no_os_remaining()
    {
        var s = PowerStatusDecoder.Decode(Now, new(1, 8, 40, 0, 0xFFFFFFFF, 0xFFFFFFFF),
            new BatteryReportRaw("Charging", 30000, 20000, 50000, 56000));
        Assert.Equal(ChargeState.Charging, s.State);
        Assert.Null(s.DischargePowerMW);
        Assert.Null(s.OsRemainingSeconds);
    }

    [Fact]
    public void Without_report_state_comes_from_flags_and_battery_saver_is_read()
    {
        var s = PowerStatusDecoder.Decode(Now, new(0, 0, 50, 1, 3600, 0xFFFFFFFF), null);
        Assert.Equal(ChargeState.Discharging, s.State);
        Assert.True(s.BatterySaver);
        Assert.Null(s.RemainingMWh);
    }

    [Fact]
    public void Zero_capacities_are_unknown()
    {
        var s = PowerStatusDecoder.Decode(Now, new(0, 0, 50, 0, 3600, 0xFFFFFFFF),
            new BatteryReportRaw("Discharging", 0, 0, 0, 0));
        Assert.Null(s.RemainingMWh);
        Assert.Null(s.DischargePowerMW);
        Assert.Null(s.HealthPct);
    }
}
