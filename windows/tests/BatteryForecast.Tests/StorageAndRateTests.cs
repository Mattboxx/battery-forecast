using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Tests;

static class Samples
{
    public static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public static BatterySample Discharging(int minute, double level, int? mw = null, int? remaining = null, int? full = null, int? design = null) =>
        new(T0.AddMinutes(minute), ChargeState.Discharging, level, false, remaining, full, design, mw);

    public static BatterySample OnAc(int minute, double level) =>
        new(T0.AddMinutes(minute), ChargeState.Charging, level, true);
}

public sealed class HistoryStoreTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"tilldusk-test-{Guid.NewGuid():N}.db");
    readonly HistoryStore store;

    public HistoryStoreTests() => store = new HistoryStore(path);

    public void Dispose()
    {
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
    }

    [Fact]
    public void Duplicates_are_ignored_and_no_battery_is_not_stored()
    {
        Assert.True(store.Append(Samples.Discharging(0, 80)));
        Assert.False(store.Append(Samples.Discharging(0, 79)));
        Assert.False(store.Append(new BatterySample(Samples.T0.AddMinutes(1), ChargeState.NotPresent, null, true)));
        Assert.Single(store.Samples(Samples.T0.AddDays(-1)));
    }

    [Fact]
    public void Model_samples_are_five_minute_bins_with_ac_if_plugged_in_the_bin()
    {
        for (var m = 0; m < 10; m++) store.Append(Samples.Discharging(m, 80 - m));
        store.Append(Samples.OnAc(10, 70));
        var bins = store.ModelSamples(Samples.T0.AddDays(-1));
        Assert.Equal(3, bins.Count);
        Assert.Equal(78, bins[0].Level);         // media di 80..76
        Assert.Equal(0, bins[0].Ac);
        Assert.Equal(1, bins[2].Ac);
        Assert.Equal(0, bins[0].Ts % 300);
    }

    [Fact]
    public void Nulls_round_trip()
    {
        store.Append(new BatterySample(Samples.T0, ChargeState.Unknown, null, null));
        var s = Assert.Single(store.Samples(Samples.T0.AddDays(-1)));
        Assert.Null(s.LevelPct);
        Assert.Null(s.OnAc);
        Assert.Null(s.BatterySaver);
    }

    [Fact]
    public void Reopen_keeps_data_and_prune_removes_old_rows()
    {
        store.Append(Samples.Discharging(0, 80));
        store.Append(Samples.Discharging(60 * 24 * 100, 50));
        using (var again = new HistoryStore(path))
        {
            Assert.Equal(2, again.Samples(Samples.T0.AddDays(-1)).Count);
            again.Prune(Samples.T0.AddDays(100), keepDays: 90);
            Assert.Single(again.Samples(Samples.T0.AddDays(-1)));
        }
    }

    [Fact]
    public void Export_writes_header_and_rows_with_invariant_culture()
    {
        store.Append(Samples.Discharging(0, 80.5));
        var w = new StringWriter();
        Assert.Equal(1, store.ExportCsv(w));
        var lines = w.ToString().Trim().Split('\n');
        Assert.StartsWith("timestamp_utc,", lines[0]);
        Assert.Contains(",80.5,0,", lines[1]);
    }

    [Fact]
    public void Wipe_removes_everything()
    {
        store.Append(Samples.Discharging(0, 80));
        store.Wipe();
        Assert.Empty(store.Samples(Samples.T0.AddDays(-1)));
    }
}

public class RecentRateTests
{
    [Fact]
    public void Power_method_uses_energy_above_reserve()
    {
        var list = Enumerable.Range(0, 10).Select(m => Samples.Discharging(m, 50, mw: 10_000, remaining: 25_000, full: 50_000)).ToList();
        var e = RecentRate.Estimate(list, reservePct: 5)!;
        Assert.Equal(RecentMethod.Power, e.Method);
        // (25000 - 2500) mWh / 10000 mW = 2,25 h
        Assert.Equal(Samples.T0.AddMinutes(9).AddHours(2.25), e.ReserveAt);
        Assert.Equal(20, e.PctPerHour, 6);
    }

    [Fact]
    public void Slope_method_is_robust_to_one_glitch()
    {
        var list = Enumerable.Range(0, 30).Select(m => Samples.Discharging(m, 60 - m * 0.2)).ToList();
        list[15] = Samples.Discharging(15, 40);       // salto del sensore
        var e = RecentRate.Estimate(list, 5)!;
        Assert.Equal(RecentMethod.Slope, e.Method);
        Assert.Equal(12, e.PctPerHour, 1);
    }

    [Fact]
    public void Tail_stops_at_charger_gap_and_rise()
    {
        var list = new List<BatterySample> { Samples.Discharging(0, 90), Samples.OnAc(1, 91) };
        list.AddRange(Enumerable.Range(2, 5).Select(m => Samples.Discharging(m, 91)));
        Assert.Equal(5, RecentRate.CurrentDischargeTail(list).Count);

        var gap = new List<BatterySample> { Samples.Discharging(0, 90), Samples.Discharging(60, 80), Samples.Discharging(61, 80) };
        Assert.Equal(2, RecentRate.CurrentDischargeTail(gap).Count);
    }

    [Fact]
    public void One_percent_drop_is_not_enough()
    {
        var list = Enumerable.Range(0, 30).Select(m => Samples.Discharging(m, m < 15 ? 60 : 59)).ToList();
        Assert.Null(RecentRate.Estimate(list, 5));
    }

    [Fact]
    public void On_ac_or_below_reserve_gives_nothing()
    {
        Assert.Null(RecentRate.Estimate([Samples.OnAc(0, 50)], 5));
        Assert.Null(RecentRate.Estimate([Samples.Discharging(0, 4)], 5));
    }
}
