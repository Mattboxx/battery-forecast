using Microsoft.Extensions.Time.Testing;
using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Tests;

sealed class FakeBattery(FakeTimeProvider clock) : IBatteryProvider
{
    public double Level = 60;
    public bool OnAc;
    public bool Held;                // collegato ma con la carica ferma (limite di carica)
    public bool? Saver = false;
    public BatterySample Read() => new(clock.GetUtcNow(), OnAc ? Held ? ChargeState.Idle : ChargeState.Charging : ChargeState.Discharging, Level, OnAc,
        BatterySaver: Saver);
}

sealed class FakeEngine : IForecastEngine
{
    public int Calls;
    public bool Fail;
    public double DropPerStep = 0.5;
    public WorkerReady? Info => new("fake", "0", 1);
    public bool IsWarm => true;
    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public int ChargeCalls;
    public double RisePerStep = 2;

    public Task<ForecastResponse> ForecastAsync(ForecastRequest r, CancellationToken ct = default)
    {
        Calls++;
        if (Fail) throw new ForecastWorkerException("exited", "boom", true);
        var now = r.Now.ToUnixTimeSeconds();
        if (r.Charge)
        {
            ChargeCalls++;
            var up = Enumerable.Range(1, 288).Select(i =>
            {
                var v = Math.Min(100, r.Level + RisePerStep * i);
                return new CurvePointDto(now + 300 * i, Math.Max(r.Level, v - 3), v, Math.Min(100, v + 3));
            }).ToList();
            var reach = up.FirstOrDefault(p => p.P50 >= r.TargetPct);
            return Task.FromResult(new ForecastResponse
            {
                ContextEndUtc = now, GeneratedAtUtc = now, Level = r.Level, ReservePct = r.TargetPct, TargetPct = r.TargetPct,
                Scenario = "charge", Source = "fake",
                Quality = new QualityDto { State = "ok", ObservedDays = 5, ChargeSessions = 5 },
                Crossing = new CrossingDto { Status = reach is null ? "beyond_horizon" : "within_horizon", CentralUtc = reach?.T },
                Curve = up,
            });
        }
        var curve = Enumerable.Range(1, 288).Select(i =>
        {
            var v = Math.Max(0, r.Level - DropPerStep * i);
            return new CurvePointDto(now + 300 * i, Math.Max(0, v - 2), v, Math.Min(r.Level, v + 2));
        }).ToList();
        var cross = curve.FirstOrDefault(p => p.P50 <= r.ReservePct);
        var early = curve.FirstOrDefault(p => p.P10 <= r.ReservePct);
        return Task.FromResult(new ForecastResponse
        {
            ContextEndUtc = now, GeneratedAtUtc = now, Level = r.Level, ReservePct = r.ReservePct, Source = "fake",
            Quality = new QualityDto { State = "ok", ObservedDays = 5, DischargeSessions = 5 },
            Crossing = new CrossingDto { Status = cross is null ? "beyond_horizon" : "within_horizon", CentralUtc = cross?.T, EarlyUtc = early?.T },
            Curve = curve,
        });
    }
}

public sealed class CoordinatorTests : IDisposable
{
    // domenica 25 ottobre 2026, 10:00 a Roma (ora solare, UTC+1)
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    readonly TimeZoneInfo rome = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    readonly string dir = Path.Combine(Path.GetTempPath(), $"tilldusk-coord-{Guid.NewGuid():N}");
    readonly HistoryStore store;
    readonly FakeBattery battery;
    readonly FakeEngine engine = new();
    readonly Collector collector;
    Settings settings = new();
    ForecastCoordinator? coord;

    public CoordinatorTests()
    {
        Directory.CreateDirectory(dir);
        store = new HistoryStore(Path.Combine(dir, "h.db"));
        battery = new FakeBattery(clock);
        collector = new Collector(battery, store, clock);
    }

    ForecastCoordinator Start()
    {
        coord = new ForecastCoordinator(collector, store, engine, () => settings, Path.Combine(dir, "cache.json"), clock, rome);
        coord.Start();
        return coord;
    }

    static DashboardState Wait(ForecastCoordinator c, Func<DashboardState, bool> ok)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (c.State is { } s && ok(s)) return s;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"state not reached: {c.State}");
    }

    void Minutes(int m)
    {
        for (var i = 0; i < m; i++) clock.Advance(TimeSpan.FromMinutes(1));    // il collector campiona ogni minuto
    }

    [Fact]
    public void On_battery_a_forecast_is_requested_and_fresh()
    {
        var c = Start();
        var s = Wait(c, s => s.Forecast is not null && !s.Updating);
        Assert.True(s.ForecastFresh);
        Assert.Equal(PowerPhase.Discharging, s.Phase);
        Assert.Equal(CrossingStatus.WithinHorizon, s.Forecast!.Crossing);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public void Charging_requests_a_charge_forecast_and_hides_the_discharge_one()
    {
        battery.OnAc = true;
        var c = Start();
        var s = Wait(c, s => s.ChargeForecast is not null && !s.Updating);
        Assert.Equal(PowerPhase.Charging, s.Phase);
        Assert.Null(s.Forecast);
        Assert.Null(s.Recent);
        Assert.True(s.ChargeForecast!.IsCharge);
        Assert.Equal(80, s.ChargeForecast.ReservePct);                    // obiettivo di carica predefinito
        Assert.Equal(CrossingStatus.WithinHorizon, s.ChargeForecast.Crossing);
        Assert.Equal(1, engine.ChargeCalls);
    }

    [Fact]
    public void At_or_above_the_charge_target_the_model_is_not_started()
    {
        battery.OnAc = true;
        battery.Level = 85;
        var c = Start();
        Wait(c, _ => true);
        Minutes(40);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public void Held_charge_shows_the_unplug_now_forecast_only_when_asked()
    {
        battery.OnAc = true;
        battery.Held = true;
        battery.Level = 79;
        var c = Start();
        Wait(c, s => s.Phase == PowerPhase.PluggedIn);
        Minutes(40);
        Assert.Equal(0, engine.Calls);                                    // il timer non avvia il modello
        c.PanelOpened();
        var s = Wait(c, s => s.HeldForecast is not null && !s.Updating);
        Assert.True(s.HeldForecastFresh);
        Assert.False(s.HeldForecast!.IsCharge);                           // scenario di scarica dal livello attuale
        Assert.Equal(5, s.HeldForecast.ReservePct);
        Assert.Null(s.Forecast);
        Assert.Equal(0, engine.ChargeCalls);
        Minutes(60);
        Assert.Equal(1, engine.Calls);
        battery.Level = 70;                                               // livello lontano: la previsione non vale più
        Minutes(1);
        Assert.Null(Wait(c, s => s.Battery.LevelPct == 70).HeldForecast);
    }

    [Fact]
    public void Unplugging_invalidates_the_charge_forecast()
    {
        battery.OnAc = true;
        var c = Start();
        Wait(c, s => s.ChargeForecast is not null && !s.Updating);
        battery.OnAc = false;
        Minutes(1);
        var s = Wait(c, s => s.Phase == PowerPhase.Discharging && s.Forecast is not null && !s.Updating);
        Assert.Null(s.ChargeForecast);
    }

    [Fact]
    public void Frequent_refreshes_every_ten_minutes_or_after_three_point_drift()
    {
        settings = settings with { Refresh = RefreshPolicy.Frequent };
        var c = Start();
        Wait(c, s => s.Forecast is not null && !s.Updating);
        Minutes(9);
        Assert.Equal(1, engine.Calls);
        Assert.NotNull(c.State!.Forecast);
        Minutes(1);
        Wait(c, _ => engine.Calls == 2);
        battery.Level -= 3;
        Minutes(2);
        Wait(c, _ => engine.Calls == 3);
    }

    [Fact]
    public void Balanced_default_runs_the_model_at_most_every_thirty_minutes()
    {
        var c = Start();
        Wait(c, s => s.Forecast is not null && !s.Updating);
        battery.Level -= 4;                                 // scarto sotto i 6 punti: niente ricalcolo
        Minutes(29);
        Assert.Equal(1, engine.Calls);
        Assert.False(c.State!.ForecastFresh);               // oltre 10 minuti: attenuata nel pannello
        Minutes(1);
        Wait(c, _ => engine.Calls == 2);
    }

    [Fact]
    public void Panel_open_refreshes_only_a_stale_forecast()
    {
        var c = Start();
        Wait(c, s => s.Forecast is not null && !s.Updating);
        Minutes(5);
        c.PanelOpened();
        Thread.Sleep(200);
        Assert.Equal(1, engine.Calls);                      // 5 minuti: ancora fresca
        Minutes(6);
        c.PanelOpened();
        Wait(c, _ => engine.Calls == 2);
    }

    [Fact]
    public void On_open_policy_and_battery_saver_never_start_the_model_by_themselves()
    {
        settings = settings with { Refresh = RefreshPolicy.OnOpen };
        var c = Start();
        Minutes(60);
        Assert.Equal(0, engine.Calls);
        c.PanelOpened();
        Wait(c, _ => engine.Calls == 1);

        settings = settings with { Refresh = RefreshPolicy.Balanced };
        battery.Saver = true;
        Minutes(120);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public void Unplugging_forces_a_new_forecast_and_charging_invalidates_the_old_one()
    {
        var c = Start();
        Wait(c, s => s.Forecast is not null && !s.Updating);
        battery.OnAc = true;
        Minutes(1);
        Assert.Null(Wait(c, s => s.Phase == PowerPhase.Charging).Forecast);
        Wait(c, _ => engine.ChargeCalls == 1);                            // collegato: previsione di carica
        battery.OnAc = false;
        Minutes(1);
        Wait(c, s => engine.Calls - engine.ChargeCalls == 2 && s.Forecast is not null);
    }

    [Fact]
    public void A_different_reserve_hides_the_cached_forecast_until_recomputed()
    {
        var c = Start();
        Wait(c, s => s.Forecast is not null && !s.Updating);
        engine.Fail = true;
        settings = settings with { ReservePct = 10 };
        c.SettingsChanged();
        var s = Wait(c, s => s.Engine == EngineState.Unavailable);
        Assert.Null(s.Forecast);
        Assert.Equal("exited", s.EngineError);
    }

    [Fact]
    public void Cache_is_shown_at_startup_until_three_hours_old()
    {
        var c = Start();
        Wait(c, s => s.Forecast is not null && !s.Updating);
        c.Dispose();
        collector.Dispose();
        engine.Fail = true;
        using var collector2 = new Collector(battery, store, clock);
        clock.Advance(TimeSpan.FromHours(1));
        using var c2 = new ForecastCoordinator(collector2, store, engine, () => settings, Path.Combine(dir, "cache.json"), clock, rome);
        c2.Start();
        var s = Wait(c2, s => !s.Updating && s.Engine == EngineState.Unavailable);
        Assert.NotNull(s.Forecast);
        Assert.False(s.ForecastFresh);
    }

    [Fact]
    public void Saving_suggestion_when_central_estimate_ends_before_target()
    {
        engine.DropPerStep = 1;           // 60% → 5% in 55 passi (~4,6 h): prima delle 22
        var c = Start();
        var s = Wait(c, s => s.Forecast is not null && !s.Updating);
        Assert.Equal(SavingSuggestion.Likely, s.Saving);
        battery.Saver = true;
        collector.Sample();
        Assert.Equal(SavingSuggestion.None, c.State!.Saving);
    }

    [Fact]
    public void Target_point_is_interpolated_at_the_evening_time()
    {
        engine.DropPerStep = 0.05;
        var c = Start();
        var s = Wait(c, s => s.Forecast is not null && !s.Updating);
        Assert.False(s.Target.Tomorrow);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.FromHours(1)), s.Target.When);
        // 12 ore = 144 passi da 0,05 → 52,8%
        Assert.Equal(52.8, s.AtTarget!.Value.P50, 1);
    }

    [Fact]
    public void Evening_target_can_be_disabled_without_running_model_again()
    {
        settings = settings with { AdditionalTargets = [new(18, 30)] };
        var c = Start();
        var first = Wait(c, s => s.Forecast is not null && !s.Updating);
        Assert.NotNull(first.AtTarget);
        settings = settings with { EveningTargetEnabled = false };
        c.SettingsChanged(recalculate: false);
        var off = Wait(c, s => !s.EveningTargetEnabled);
        Assert.Null(off.AtTarget);
        Assert.Empty(off.AdditionalTargets);
        Assert.Equal(SavingSuggestion.None, off.Saving);
        Assert.Same(first.Forecast, off.Forecast);
        Assert.Equal(1, engine.Calls);
        settings = settings with { EveningTargetEnabled = true };
        c.SettingsChanged(recalculate: false);
        Assert.NotNull(Wait(c, s => s.EveningTargetEnabled).AtTarget);
        Assert.Equal(new TimeOnly(18, 30), settings.AdditionalTargets[0]);
    }

    [Fact]
    public void Manual_recalculation_reads_current_level_even_with_on_open_and_battery_saver()
    {
        settings = settings with { Refresh = RefreshPolicy.OnOpen };
        battery.Saver = true;
        var c = Start();
        Assert.Equal(0, engine.Calls);
        battery.Level = 43;
        c.RecalculateNow();
        var state = Wait(c, s => s.Forecast is not null && !s.Updating);
        Assert.Equal(43, state.Battery.LevelPct);
        Assert.Equal(43, state.Forecast!.Level);
        Assert.Equal(1, engine.Calls);
    }

    public void Dispose()
    {
        coord?.Dispose();
        collector.Dispose();
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

public class TargetTimeTests
{
    static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    [Fact]
    public void After_target_moves_to_tomorrow_and_says_so()
    {
        var t = TargetTime.Next(new DateTimeOffset(2026, 10, 25, 21, 30, 0, TimeSpan.Zero), new(22, 0), Rome);
        Assert.True(t.Tomorrow);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 22, 0, 0, TimeSpan.FromHours(1)), t.When);
    }

    [Fact]
    public void Nonexistent_local_time_on_dst_day_is_shifted_forward()
    {
        // 29 marzo 2026: a Roma le 02:30 non esistono
        var t = TargetTime.Next(new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero), new(2, 30), Rome);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), t.When);
    }

    [Fact]
    public void Forecast_view_interpolates_from_current_level()
    {
        var g = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var f = new ForecastView(g, 50, 5, CrossingStatus.BeyondHorizon, null, null, null,
            [new(g.AddMinutes(5), 48, 49, 50), new(g.AddMinutes(10), 46, 48, 50)], HistoryQuality.Ok, 1, 1, null, null);
        Assert.Equal(49.5, f.At(g.AddMinutes(2.5))!.Value.P50, 6);
        Assert.Null(f.At(g.AddMinutes(11)));
        Assert.Null(f.At(g.AddMinutes(-1)));
    }
}
