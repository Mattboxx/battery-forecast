using System.Globalization;
using System.Resources;
using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.Tests;

[Collection("culture")]   // L.Culture è globale: questi test non girano in parallelo fra loro
public class PresenterTests
{
    static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);   // 10:00 a Roma

    public PresenterTests()
    {
        L.Culture = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
    }

    static BatterySample Bat(double? level = 60, bool? ac = false, ChargeState st = ChargeState.Discharging, int? os = null) =>
        new(Now, st, level, ac, OsRemainingSeconds: os);

    static ForecastView View(CrossingStatus c = CrossingStatus.WithinHorizon, HistoryQuality q = HistoryQuality.Ok,
        double hours = 3.33, double? early = 2.8, double? late = 3.75) =>
        new(Now, 60, 5, c, c == CrossingStatus.WithinHorizon ? Now.AddHours(hours) : null,
            early is double e ? Now.AddHours(e) : null, late is double l ? Now.AddHours(l) : null,
            [.. Enumerable.Range(1, 288).Select(i => new CurvePoint(Now.AddMinutes(5 * i), Math.Max(0, 58 - i * 0.1), Math.Max(0, 60 - i * 0.1), 60))],
            q, 6.2, 9, "mistralai/Timestral-28M", "49b2640");

    static DashboardState State(BatterySample b, ForecastView? f = null, RecentEstimate? r = null, bool fresh = true,
        string? err = null, bool updating = false) => new()
    {
        Now = Now,
        Battery = b,
        Phase = b switch
        {
            { HasBattery: false } => PowerPhase.NoBattery,
            { LevelPct: null } => PowerPhase.SensorUnavailable,
            { OnAc: false } => PowerPhase.Discharging,
            { State: ChargeState.Charging } => PowerPhase.Charging,
            _ => PowerPhase.PluggedIn,
        },
        Forecast = f,
        ForecastFresh = fresh,
        Recent = r,
        EngineError = err,
        Engine = err is null ? EngineState.Ready : EngineState.Unavailable,
        Updating = updating,
        Target = TargetTime.Next(Now, new(22, 0), Rome),
        AtTarget = f?.At(TargetTime.Next(Now, new(22, 0), Rome).When),
        ReservePct = 5,
    };

    [Fact]
    public void Habit_forecast_shows_until_range_reserve_and_source()
    {
        var m = PanelPresenter.Present(State(Bat(), View(), new RecentEstimate(RecentMethod.Slope, 20, Now.AddHours(2.75), TimeSpan.FromMinutes(30))), Rome);
        Assert.Equal("Fino alle 13:20", m.Headline);
        Assert.Equal("circa 3 h 20 min · fascia indicativa 12:50–13:45", m.Detail);
        Assert.Contains("riserva del 5%", m.Note);
        Assert.Equal(EstimateSource.Habits, m.Source);
        Assert.Equal("Impara dal tuo utilizzo · 6,2 giorni di storia", m.SourceLine);
        Assert.Equal("Al ritmo attuale: circa 2 h 45 min (fino alle 12:45)", m.RecentLine);
        Assert.Equal("Alle 22:00", m.TargetLabel);
        Assert.Equal("46%", m.TargetValue);
        Assert.NotNull(m.ChartSummary);
        Assert.True(m.Tooltip.Length <= 127);
        Assert.Contains("circa 3 h 20 min rimanenti", m.Tooltip);
    }

    [Fact]
    public void Habitual_recharge_is_explained()
    {
        var f = View() with { HabitualRecharge = Now.AddHours(2.1), ExtrapolatedFrom = Now.AddHours(2.1) };
        Assert.Equal("Di solito colleghi l'alimentatore verso le 12:05: oltre, la curva prosegue al ritmo medio previsto.",
            PanelPresenter.Present(State(Bat(), f), Rome).RechargeLine);
        Assert.Contains("non dicono", PanelPresenter.Present(State(Bat(), f with { ExtrapolatedFrom = null }), Rome).RechargeLine);
        Assert.Null(PanelPresenter.Present(State(Bat(), View()), Rome).RechargeLine);
    }

    [Fact]
    public void Target_below_reserve_is_said_not_shown_as_zero()
    {
        var f = View() with { Curve = [.. View().Curve.Select(p => p with { P10 = 0, P50 = 0, P90 = 3 })] };
        var s = State(Bat(), f) with { AtTarget = f.At(TargetTime.Next(Now, new(22, 0), Rome).When) };
        var m = PanelPresenter.Present(s, Rome);
        Assert.Null(m.TargetValue);
        Assert.Equal("sotto la riserva del 5%", m.TargetRange);
    }

    [Fact]
    public void Range_on_another_day_names_the_day_once()
    {
        var f = View(hours: 16, early: 14.2, late: 22.7);    // 00:15 e 08:45 di domani (ora di Roma)
        Assert.Equal("circa 16 h · fascia indicativa domani 00:10–08:40", PanelPresenter.Present(State(Bat(), f), Rome).Detail);
    }

    [Fact]
    public void Beyond_horizon_never_claims_exactly_24_hours()
    {
        var m = PanelPresenter.Present(State(Bat(), View(CrossingStatus.BeyondHorizon, early: null)), Rome);
        Assert.Equal("Oltre le prossime 24 ore", m.Headline);
        Assert.Equal("Anche nella stima più prudente resta sopra il 5%", m.Detail);
    }

    [Fact]
    public void Insufficient_history_falls_back_to_recent_and_says_so()
    {
        var r = new RecentEstimate(RecentMethod.Power, 15, Now.AddHours(15), TimeSpan.FromMinutes(10));
        var m = PanelPresenter.Present(State(Bat(), View(q: HistoryQuality.Insufficient), r), Rome);
        Assert.Equal(EstimateSource.Recent, m.Source);
        Assert.Equal("Fino a domani alle 01:00", m.Headline);
        Assert.Contains("consumo recente", m.SourceLine);
        Assert.Null(m.Chart);
    }

    [Fact]
    public void First_run_says_collecting()
    {
        var m = PanelPresenter.Present(State(Bat()), Rome);
        Assert.Equal("Raccolta dati in corso", m.Headline);
        Assert.Equal(EstimateSource.None, m.Source);
    }

    [Fact]
    public void Missing_engine_is_explained_without_retry_button()
    {
        var m = PanelPresenter.Present(State(Bat(), err: "not_found"), Rome);
        Assert.Contains("non è installato", m.Note);
        Assert.False(m.CanRetry);
        Assert.True(PanelPresenter.Present(State(Bat(), err: "exited"), Rome).CanRetry);
    }

    [Fact]
    public void Charging_never_shows_old_discharge_forecast()
    {
        var m = PanelPresenter.Present(State(Bat(73, true, ChargeState.Charging), View()), Rome);
        Assert.Equal("In carica · 73%", m.Headline);
        Assert.Null(m.Chart);
        Assert.Contains("stacchi l'alimentatore", m.Note);
        Assert.Equal("Fonte di alimentazione: Alimentatore", m.PowerLine);
    }

    [Fact]
    public void No_battery_and_sensor_unavailable()
    {
        Assert.Equal("Nessuna batteria trovata", PanelPresenter.Present(State(new(Now, ChargeState.NotPresent, null, true)), Rome).Headline);
        Assert.Equal("Lettura della batteria non disponibile", PanelPresenter.Present(State(Bat(null, null, ChargeState.Unknown)), Rome).Headline);
    }

    [Fact]
    public void Stale_forecast_is_marked()
    {
        Assert.True(PanelPresenter.Present(State(Bat(), View(), fresh: false), Rome).Stale);
    }

    [Theory]
    [InlineData(44, "44 min")]
    [InlineData(61, "1 h")]
    [InlineData(200, "3 h 20 min")]
    [InlineData(-5, "0 min")]
    public void Durations_round_to_five_minutes_over_an_hour(int minutes, string text) =>
        Assert.Equal(text, PanelPresenter.Duration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void English_texts()
    {
        L.Culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        try
        {
            var m = PanelPresenter.Present(State(Bat(), View()), Rome);
            Assert.Equal("Until 13:20", m.Headline);
            Assert.Equal("Learns from your use · 6.2 days of history", m.SourceLine);
        }
        finally { L.Culture = CultureInfo.GetCultureInfo("it-IT"); }
    }

    [Fact]
    public void Every_key_exists_in_both_languages_with_same_placeholders()
    {
        var rm = new ResourceManager("BatteryForecast.Core.Resources.Strings", typeof(L).Assembly);
        var en = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var it = rm.GetResourceSet(CultureInfo.GetCultureInfo("it"), true, false)!;
        var count = 0;
        foreach (System.Collections.DictionaryEntry e in en)
        {
            var key = (string)e.Key;
            var other = it.GetString(key);
            Assert.False(other is null, key);
            static string Holes(string s) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(x => x.Value).Order());
            Assert.Equal(Holes((string)e.Value!), Holes(other!));
            count++;
        }
        Assert.True(count > 50);
    }
}

[Collection("culture")]
public class HistoryPresenterTests
{
    static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public HistoryPresenterTests() => L.Culture = CultureInfo.GetCultureInfo("it-IT");

    static BatterySample S(int minute, double level, bool ac = false, int? remaining = null) =>
        new(T0.AddMinutes(minute), ac ? ChargeState.Charging : ChargeState.Discharging, level, ac,
            remaining, 50_000, 60_000);

    [Fact]
    public void Segments_split_at_suspend_and_charger_and_newest_first()
    {
        var samples = new List<BatterySample>();
        for (var m = 0; m <= 60; m++) samples.Add(S(m, 90 - m * 0.2, remaining: 45_000 - m * 100));
        samples.Add(S(61, 78, ac: true));
        for (var m = 120; m <= 150; m++) samples.Add(S(m, 80 - (m - 120) * 0.3));
        var model = HistoryPresenter.Present(samples, [T0.AddMinutes(135)], null, Rome);
        Assert.Equal(3, model.Segments.Count);                           // la sospensione alle 135 divide il secondo tratto
        Assert.Equal("90% → 78% in 1 h", model.Segments[^1].Change);
        Assert.Equal("12,0%/h", model.Segments[^1].Rate);
        Assert.Equal("6,0 Wh", model.Segments[^1].Energy);
        Assert.Null(model.Segments[0].Energy);
        Assert.Equal("dom 25 ott, 10:00", model.Segments[^1].Day);                           // senza capacità residua non si stima l'energia
    }

    [Fact]
    public void Health_from_capacities_with_source_and_unknown_case()
    {
        var m = HistoryPresenter.Present([S(0, 50)], [], S(0, 50), Rome);
        Assert.Equal("83% della capacità di progetto", m.HealthValue);
        Assert.StartsWith("50 Wh a piena carica su 60 Wh di progetto", m.HealthDetail);
        var unknown = HistoryPresenter.Present([], [], new BatterySample(T0, ChargeState.Discharging, 50, false), Rome);
        Assert.Null(unknown.HealthValue);
        Assert.Contains("non comunica", unknown.HealthDetail);
        Assert.Empty(unknown.Segments);
    }
}
