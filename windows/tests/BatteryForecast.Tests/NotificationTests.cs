using Microsoft.Extensions.Time.Testing;
using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Notifications;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Tests;

public sealed class NotificationTests : IDisposable
{
    readonly FakeTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
    readonly string path = Path.Combine(Path.GetTempPath(), $"tilldusk-notify-{Guid.NewGuid():N}.db");
    readonly HistoryStore store;
    NotificationRules rules;
    readonly NotificationSettings settings = new() { ChargedPct = 80 };

    public NotificationTests()
    {
        store = new HistoryStore(path);
        rules = new NotificationRules(store, clock);
    }

    public void Dispose()
    {
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
    }

    BatterySample Bat(double level, bool ac = false, int? mw = null) =>
        new(clock.GetUtcNow(), ac ? ChargeState.Charging : ChargeState.Discharging, level, ac, DischargePowerMW: mw);

    BatteryNotice? Eval(double level, double? minutesLeft = null, bool ac = false, bool fresh = true, bool demo = false)
    {
        var b = Bat(level, ac);
        if (ac) store.Append(b);
        var reserveAt = minutesLeft is double m ? clock.GetUtcNow().AddMinutes(m) : (DateTimeOffset?)null;
        return rules.Evaluate(new NotificationInput(b, reserveAt, fresh, "habits", [], demo), settings);
    }

    void Advance(int minutes) => clock.Advance(TimeSpan.FromMinutes(minutes));

    [Fact]
    public void Each_time_threshold_fires_once_per_discharge()
    {
        Assert.Equal("minutes:60", Eval(50, 58)?.Rule);
        Advance(6);
        Assert.Null(Eval(49, 52));
        Advance(6);
        Assert.Null(Eval(48, 45));
        Advance(20);
        Assert.Equal("minutes:30", Eval(40, 29)?.Rule);
    }

    [Fact]
    public void Jumping_over_several_thresholds_gives_one_message_and_consumes_the_others()
    {
        var n = Eval(30, 12);
        Assert.Equal("minutes:15", n?.Rule);
        Assert.Equal(10, n!.Value);
        Advance(10);
        Assert.Null(Eval(29, 50));          // la stima risale: le soglie già superate non si ripetono
    }

    [Fact]
    public void No_alerts_with_stale_estimate_on_ac_or_in_demo()
    {
        Assert.Null(Eval(50, 20, fresh: false));
        Assert.Null(Eval(50, 20, demo: true));
        Assert.Null(Eval(50, 20, ac: true));
    }

    [Fact]
    public void Charging_starts_a_new_discharge_session()
    {
        Assert.Equal("minutes:60", Eval(50, 55)?.Rule);
        Advance(10);
        Eval(60, ac: true);
        Advance(10);
        Assert.Equal("minutes:60", Eval(58, 55)?.Rule);
    }

    [Fact]
    public void Low_percentage_once_and_minimum_interval_between_alerts()
    {
        Assert.Equal("low", Eval(10)?.Rule);
        Advance(1);
        Assert.Null(Eval(9, 50));           // meno di 5 minuti dall'ultimo avviso
        Advance(5);
        Assert.Equal("minutes:60", Eval(9, 50)?.Rule);
        Advance(6);
        Assert.Null(Eval(8));
    }

    [Fact]
    public void Charged_alert_rearms_only_after_a_real_drop()
    {
        Assert.Equal("charged", Eval(80, ac: true)?.Rule);
        Advance(6);
        Assert.Null(Eval(79, ac: true));
        Advance(6);
        Assert.Null(Eval(81, ac: true));    // oscillazione intorno alla soglia
        Advance(6);
        Eval(74);                           // scarica di oltre 5 punti
        Advance(6);
        Assert.Equal("charged", Eval(80, ac: true)?.Rule);
    }

    [Fact]
    public void Memory_survives_a_restart()
    {
        Assert.Equal("minutes:60", Eval(50, 55)?.Rule);
        rules = new NotificationRules(store, clock);
        Advance(10);
        Assert.Null(Eval(49, 50));
    }

    [Fact]
    public void Disabled_means_silent()
    {
        var off = settings with { Enabled = false };
        var b = Bat(10);
        Assert.Null(rules.Evaluate(new NotificationInput(b, clock.GetUtcNow().AddMinutes(10), true, "habits", []), off));
    }

    [Fact]
    public void Sustained_drain_needs_thirty_minutes_and_a_doubling()
    {
        var t0 = clock.GetUtcNow();
        List<BatterySample> Series(int recentMw) => [.. Enumerable.Range(0, 31).Select(i =>
            new BatterySample(t0.AddMinutes(i), ChargeState.Discharging, 80 - i * 0.2, false,
                DischargePowerMW: i >= 21 ? recentMw : 6000))];
        Assert.True(NotificationRules.SustainedDrain(Series(15000)));
        Assert.False(NotificationRules.SustainedDrain(Series(9000)));      // non raddoppia
        Assert.False(NotificationRules.SustainedDrain(Series(15000)[..20]));
    }
}
