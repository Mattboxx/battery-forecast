using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace BatteryForecast.Tests;

public class ContextOptionsTests
{
    [Fact]
    public void Options_roundtrip_and_old_settings_keep_their_target_enabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bf-options-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"TargetTime\":\"20:15:00\"}");
            var old = Settings.Load(path);
            Assert.True(old.EveningTargetEnabled);
            Assert.True(old.TrendShowObserved);
            Assert.True(old.TrendShowForecast);
            var settings = old with { EveningTargetEnabled = false, CollectAdvancedSensors = false,
                ContextIntervalSeconds = 120, ContextHistoryHours = 48, TrendShowObserved = false, TrendShowForecast = false };
            settings.Save(path);
            var loaded = Settings.Load(path);
            Assert.False(loaded.EveningTargetEnabled);
            Assert.False(loaded.TrendShowObserved);
            Assert.False(loaded.TrendShowForecast);
            Assert.False(loaded.CollectAdvancedSensors);
            Assert.Equal(new TimeOnly(20, 15), loaded.TargetTime);
            Assert.Equal(120, loaded.ContextIntervalSeconds);
            Assert.Equal(48, loaded.ContextHistoryHours);
            Assert.Equal(60, (loaded with { ContextIntervalSeconds = 0 }).Normalized().ContextIntervalSeconds);
            Assert.Equal(24, (loaded with { ContextHistoryHours = 999 }).Normalized().ContextHistoryHours);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Context_interval_and_force_refresh_do_not_reduce_battery_history_or_ignore_opt_out()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bf-context-options-{Guid.NewGuid():N}.db");
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1800000000));
        bool enabled = true;
        try
        {
            using var store = new HistoryStore(path);
            var sensors = new CounterSensors();
            using var collector = new Collector(new Battery(clock), store, clock, context: sensors,
                collectContext: () => enabled, contextIntervalSeconds: () => 120);
            collector.Start();
            for (var i = 0; i < 1; i++) clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, sensors.Reads);
            Assert.Equal(2, store.Samples(DateTimeOffset.MinValue).Count);
            collector.Sample(forceContext: true);
            Assert.Equal(2, sensors.Reads);
            var summary = store.ContextSummary(DateTimeOffset.MinValue);
            Assert.Equal(2, summary.Samples);
            Assert.Equal(2, summary.Observations["cpu_pct"]);
            Assert.Equal(0, summary.Observations["cpu_temp_c"]);
            enabled = false;
            clock.Advance(TimeSpan.FromSeconds(1));
            collector.Sample(forceContext: true);
            Assert.Equal(2, sensors.Reads);
            Assert.Null(collector.CurrentContext);
            Assert.Equal(2, store.ContextSummary(DateTimeOffset.MinValue).Samples);
            enabled = true;
            clock.Advance(TimeSpan.FromSeconds(1));
            collector.Sample();
            Assert.Equal(3, sensors.Reads);
        }
        finally { foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file); }
    }
    [Fact]
    public void Context_has_an_independent_timer_at_thirty_and_ninety_seconds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bf-context-timer-{Guid.NewGuid():N}.db");
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1800000000));
        int seconds = 30;
        try
        {
            using var store = new HistoryStore(path);
            var context = new CounterSensors();
            using var collector = new Collector(new Battery(clock), store, clock, context: context, contextIntervalSeconds: () => seconds);
            collector.Start();
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(2, context.Reads);
            Assert.Single(store.Samples(DateTimeOffset.MinValue));
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(3, context.Reads);
            Assert.Equal(2, store.Samples(DateTimeOffset.MinValue).Count);
            seconds = 90;
            collector.Sample(forceContext: true);
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(4, context.Reads);
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(5, context.Reads);
            Assert.Equal(3, store.Samples(DateTimeOffset.MinValue).Count);
        }
        finally { foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file); }
    }

    sealed class Battery(FakeTimeProvider clock) : IBatteryProvider
    { public BatterySample Read() => new(clock.GetUtcNow(), ChargeState.Discharging, 60, false); }
    sealed class CounterSensors : ISystemContextProvider
    { public int Reads; public SystemSample Read(BatterySample battery) => new(battery.TimestampUtc, CpuPct: ++Reads); }
}
