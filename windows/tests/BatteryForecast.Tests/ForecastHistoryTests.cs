using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Tests;

public class ForecastHistoryTests
{
    [Fact]
    public void Recent_trend_uses_fresh_duplicate_and_stops_at_a_replaced_battery()
    {
        var history = new[] { Samples.Discharging(0, 90, design: 50000),
            Samples.Discharging(1, 80, design: 60000), Samples.Discharging(1, 79, design: 60000) };
        var tail = Assert.Single(RecentRate.CurrentDischargeTail(history));
        Assert.Equal(79, tail.LevelPct);
    }

    [Fact]
    public void Charge_trend_ignores_future_rows_and_fresh_reading_overrides_same_second()
    {
        var history = Enumerable.Range(0, 11).Select(m => Samples.OnAc(m, 60+m*.5)).ToList();
        history[^1] = Samples.OnAc(10, 40); // old row at the same timestamp
        history.Add(Samples.Discharging(11, 10)); // cannot be an input at minute 10
        var estimate = ChargeRate.Estimate(history, Samples.OnAc(10, 65), 75);
        Assert.Equal(ChargeEstimateKind.RecentRate, estimate.Kind);
        Assert.Equal(Samples.T0.AddMinutes(30), estimate.TargetAt);
    }

    [Fact]
    public void Model_snapshot_excludes_later_values_even_in_the_same_bin_and_keeps_mixed_ac_unknown()
    {
        WithStore(store =>
        {
            store.Append(Samples.Discharging(0, 90));
            store.Append(Samples.OnAc(1, 89));
            store.Append(Samples.Discharging(2, 88));
            store.AppendContext(new(Samples.T0, MemoryPct: 20));
            store.AppendContext(new(Samples.T0.AddMinutes(2), MemoryPct: 100));
            var earlier = Assert.Single(store.ModelSamples(Samples.T0, Samples.T0));
            Assert.Equal(90, earlier.Level);
            Assert.Equal(0, earlier.Ac);
            var later = Assert.Single(store.ModelSamples(Samples.T0, Samples.T0.AddMinutes(2)));
            Assert.Null(later.Ac);
            Assert.Equal(20, Assert.Single(store.ModelCovariates(Samples.T0, Samples.T0)["memory_pct"]).Value);
            Assert.Equal(3, store.Samples(Samples.T0).Count); // no history was removed
        });
    }

    [Fact]
    public void Model_history_and_context_use_the_battery_epoch_at_the_request_time()
    {
        WithStore(store =>
        {
            store.Append(Samples.Discharging(0, 90, design: 50000));
            store.AppendContext(new(Samples.T0, MemoryPct: 20));
            store.Append(Samples.Discharging(10, 70, design: 60000));
            store.AppendContext(new(Samples.T0.AddMinutes(10), MemoryPct: 80));
            var before = Assert.Single(store.ModelSamples(Samples.T0, Samples.T0.AddMinutes(5)));
            var after = Assert.Single(store.ModelSamples(Samples.T0, Samples.T0.AddMinutes(10)));
            Assert.Equal(90, before.Level);
            Assert.Equal(70, after.Level);
            Assert.Equal(80, Assert.Single(store.ModelCovariates(Samples.T0, Samples.T0.AddMinutes(10), currentBatteryOnly: true)["memory_pct"]).Value);
            Assert.Equal(2, store.Samples(Samples.T0).Count);
            Assert.Equal(2, store.ModelCovariates(Samples.T0, Samples.T0.AddMinutes(10))["cpu_pct"].Count);
        });
    }

    static void WithStore(Action<HistoryStore> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bf-snapshot-{Guid.NewGuid():N}.db");
        try { using var store = new HistoryStore(path); action(store); }
        finally { foreach (var p in new[] { path, path + "-wal", path + "-shm" }) File.Delete(p); }
    }
}
