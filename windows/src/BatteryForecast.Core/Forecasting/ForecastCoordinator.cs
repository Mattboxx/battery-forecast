using System.Text.Json;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Forecasting;

public enum PowerPhase { NoBattery, SensorUnavailable, Charging, PluggedIn, Discharging }
public enum EngineState { Off, Loading, Ready, Unavailable }

/// <summary>Proposta del risparmio energetico, come nell'originale: la previsione contiene già l'uso abituale.</summary>
public enum SavingSuggestion { None, Likely, Possible }

/// <summary>
/// Tutto ciò che la UI mostra, in un'istantanea immutabile e coerente. La UI non interroga né motore né database.
/// </summary>
public sealed record DashboardState
{
    public required DateTimeOffset Now { get; init; }
    public required BatterySample Battery { get; init; }
    public required PowerPhase Phase { get; init; }
    /// <summary>Previsione valida per adesso (a batteria, dopo l'ultima ricarica, meno di 3 ore). Null altrimenti.</summary>
    public ForecastView? Forecast { get; init; }
    /// <summary>Meno di 5 minuti e livello ancora uguale: altrimenti la UI la mostra attenuata.</summary>
    public bool ForecastFresh { get; init; }
    public SystemSample? SystemContext { get; init; }
    public RecentEstimate? Recent { get; init; }
    public ChargeEstimate? Charging { get; init; }
    /// <summary>Previsione Timestral di carica valida ora (in carica, dopo l'ultimo stacco, stesso obiettivo).</summary>
    public ForecastView? ChargeForecast { get; init; }
    public bool ChargeForecastFresh { get; init; }
    public IReadOnlyList<TargetTime> AdditionalTargets { get; init; } = [];
    public bool Updating { get; init; }
    public EngineState Engine { get; init; }
    public string? EngineError { get; init; }
    public required TargetTime Target { get; init; }
    public CurvePoint? AtTarget { get; init; }
    public SavingSuggestion Saving { get; init; }
    public DateTimeOffset? SavingAt { get; init; }
    public double ReservePct { get; init; }
    public bool Demo { get; init; }
}

/// <summary>
/// Decide quando chiedere una previsione e compone lo stato. Il modello non deve girare di continuo:
/// in background a batteria al massimo ogni 30 minuti (10 con "Frequent"), prima solo se il livello si è
/// mosso molto; subito dopo lo scollegamento o la ripresa; all'apertura del pannello se la previsione ha
/// più di 10 minuti. Con il risparmio energetico o "OnOpen" solo su richiesta dell'utente.
/// Una richiesta alla volta; al massimo un aggiornamento chiesto dall'utente in coda.
/// </summary>
public sealed class ForecastCoordinator : IDisposable
{
    static readonly TimeSpan DriftMinAge = TimeSpan.FromMinutes(2),
        FreshFor = TimeSpan.FromMinutes(10), ValidFor = TimeSpan.FromHours(3), RecentLookback = TimeSpan.FromMinutes(40),
        HistoryDays = TimeSpan.FromDays(110);
    const double FreshDrift = 2;

    /// <summary>Chi chiede la previsione: il timer, un evento (scollegamento, ripresa) o l'utente.</summary>
    enum Trigger { Background, Event, User }

    /// <summary>Intervallo e scarto di livello per gli aggiornamenti automatici; null = solo su richiesta.</summary>
    static (TimeSpan Every, double Drift)? Automatic(RefreshPolicy policy, bool saverOn) => (policy, saverOn) switch
    {
        (RefreshPolicy.OnOpen, _) or (_, true) => null,      // con il risparmio energetico il modello non parte da solo
        (RefreshPolicy.Frequent, _) => (TimeSpan.FromMinutes(10), 3),
        _ => (TimeSpan.FromMinutes(30), 6),
    };

    readonly Collector collector;
    readonly HistoryStore store;
    readonly IForecastEngine? engine;
    readonly Func<Settings> settings;
    readonly TimeProvider clock;
    readonly TimeZoneInfo zone;
    readonly string? cachePath;
    readonly bool demo;
    readonly Lock gate = new();

    ForecastView? forecast;
    DateTimeOffset? lastRun, lastAcSeen, lastChargeRun, lastBatterySeen;
    ForecastView? chargeForecast;
    bool updating, pendingForce;
    EngineState engineState = EngineState.Off;
    string? engineError;
    DashboardState? state;

    public ForecastCoordinator(Collector collector, HistoryStore store, IForecastEngine? engine, Func<Settings> settings,
        string? cachePath = null, TimeProvider? clock = null, TimeZoneInfo? zone = null, bool demo = false)
    {
        this.collector = collector;
        this.store = store;
        this.engine = engine;
        this.settings = settings;
        this.cachePath = cachePath;
        this.clock = clock ?? TimeProvider.System;
        this.zone = zone ?? TimeZoneInfo.Local;
        this.demo = demo;
        if (engine is null)
            (engineState, engineError) = (EngineState.Unavailable, "not_found");
    }

    public event Action<DashboardState>? Changed;
    public DashboardState? State { get { lock (gate) return state; } }

    public void Start()
    {
        lastAcSeen = store.LastAcTimestamp();
        forecast = LoadCache();
        lastRun = forecast?.Generated;
        collector.Sampled += OnSampled;
        collector.Start();          // prima lettura: pubblica lo stato e, a batteria, avvia il motore e la previsione
    }

    /// <summary>All'apertura del pannello i dati devono essere freschi: altrimenti parte il ricalcolo.</summary>
    public void PanelOpened() => Task.Run(() =>
    {
        collector.Sample();
        bool fresh;
        lock (gate) fresh = state is { Phase: PowerPhase.Charging } ? state.ChargeForecastFresh : state?.ForecastFresh == true;
        if (!fresh) Request(Trigger.User);
    });

    public void Resumed() => Task.Run(() =>
    {
        collector.OnResume();
        Request(Trigger.Event);
    });

    /// <summary>Le preferenze sono cambiate (riserva, obiettivo): la cache con un'altra riserva non vale più.</summary>
    public void SettingsChanged() => Task.Run(() =>
    {
        Publish();
        Request(Trigger.User);
    });

    public void Retry() => Task.Run(() => Request(Trigger.User));

    /// <summary>Lo storico è stato cancellato: si dimentica anche la previsione che ne derivava.</summary>
    public void Reset() => Task.Run(() =>
    {
        lock (gate) (forecast, lastRun, lastAcSeen, chargeForecast, lastChargeRun) = (null, null, null, null, null);
        Publish();
        Request(Trigger.User);
    });

    void OnSampled(BatterySample s, PowerChange change)
    {
        if (s.OnAc == true)
            lock (gate) lastAcSeen = s.TimestampUtc;
        else if (s.OnAc == false)
            lock (gate) lastBatterySeen = s.TimestampUtc;
        Publish();
        Request(change == PowerChange.None ? Trigger.Background : Trigger.Event);
    }

    void SetEngine(EngineState s, string? error)
    {
        lock (gate) (engineState, engineError) = (s, error);
        Publish();
    }

    void Request(Trigger trigger)
    {
        var s = collector.Current;
        if (engine is null || s is not { LevelPct: double level }) return;
        // in scarica la previsione fino alla riserva; in carica, finché l'obiettivo non è raggiunto, quella di carica
        var charge = s is { OnAc: true, State: ChargeState.Charging } && level < settings().ChargeTargetPct;
        if (!s.IsDischarging && !charge) return;
        var now = clock.GetUtcNow();
        var auto = Automatic(settings().Refresh, s.BatterySaver == true);
        if (trigger != Trigger.User && auto is null) return;       // solo su richiesta dell'utente
        var force = trigger != Trigger.Background;
        lock (gate)
        {
            if (updating)
            {
                pendingForce |= trigger == Trigger.User;
                return;
            }
            if (!force)
            {
                var age = (charge ? lastChargeRun : lastRun) is { } r ? now - r : TimeSpan.MaxValue;
                var drift = (charge ? chargeForecast : forecast) is { } f ? Math.Abs(level - f.Level) : 99;
                if (!(age >= auto!.Value.Every || (drift >= auto.Value.Drift && age >= DriftMinAge))) return;
            }
            updating = true;
            if (charge) lastChargeRun = now; else lastRun = now;
        }
        Publish();
        _ = RunAsync(s, level, now, charge);
    }

    async Task RunAsync(BatterySample s, double level, DateTimeOffset started, bool charge = false)
    {
        var reserve = charge ? settings().ChargeTargetPct : settings().ReservePct;
        try
        {
            if (!engine!.IsWarm) SetEngine(EngineState.Loading, null);
            var history = store.ModelSamples(started - HistoryDays);
            var r = await engine.ForecastAsync(new ForecastRequest(s.TimestampUtc, level, s.OnAc, reserve, history,
                settings().UseSystemContext && settings().CollectSystemContext ? store.ModelCovariates(started-HistoryDays,started) : null,
                Charge: charge, TargetPct: reserve)).ConfigureAwait(false);
            var view = ForecastView.From(r);
            lock (gate)
            {
                // Cambiata l'alimentazione mentre si calcolava: la risposta descrive uno scenario che non c'è più.
                if (!charge && lastAcSeen is { } ac && ac >= started) view = null;
                if (charge && lastBatterySeen is { } bat && bat >= started) view = null;
                if (view is not null && charge) chargeForecast = view;
                else if (view is not null) forecast = view;
                (engineState, engineError) = (EngineState.Ready, null);
            }
            if (view is not null)
            {
                if (!charge) SaveCache(r);
                try
                {
                    store.RecordForecast(view.Generated, charge ? r.Source + ":charge" : r.Source, level, reserve, r.Crossing.Status, view.Central,
                        view.Early, view.Late, r.Quality.State, r.ModelId, r.ModelRevision, r.ElapsedMs);
                }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
            }
        }
        catch (ForecastWorkerException e)
        {
            lock (gate) (engineState, engineError) = (EngineState.Unavailable, e.Code);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            lock (gate) (engineState, engineError) = (EngineState.Unavailable, e.GetType().Name);
        }
        bool again;
        lock (gate)
        {
            updating = false;
            again = pendingForce;
            pendingForce = false;
        }
        Publish();
        if (again) Request(Trigger.User);
    }

    /// <summary>Ricompone lo stato dall'ultima lettura e lo notifica.</summary>
    public void Publish()
    {
        if (collector.Current is not { } s) return;
        var cfg = settings();
        var now = clock.GetUtcNow();
        var recent = SafeRecent(s, cfg.ReservePct, now);
        ChargeEstimate? charging = null;
        if (s.OnAc == true && s.State == ChargeState.Charging && now - s.TimestampUtc <= TimeSpan.FromMinutes(2))
        {
            try { charging = ChargeRate.Estimate(store.Samples(now.AddDays(-30), now), s, cfg.ChargeTargetPct); }
            catch (Microsoft.Data.Sqlite.SqliteException) { charging = new(ChargeEstimateKind.Collecting, cfg.ChargeTargetPct); }
        }
        DashboardState next;
        lock (gate)
        {
            var active = forecast is { } f && s.IsDischarging && now - f.Generated < ValidFor
                && (lastAcSeen is not { } ac || f.Generated > ac) && Math.Abs(f.ReservePct - cfg.ReservePct) < 0.01
                ? f : null;
            var fresh = active is not null && now - active.Generated < FreshFor
                && s.LevelPct is double l && Math.Abs(l - active.Level) <= FreshDrift;
            var activeCharge = chargeForecast is { } cf && s.OnAc == true && now - cf.Generated < ValidFor
                && (lastBatterySeen is not { } bat || cf.Generated > bat) && Math.Abs(cf.ReservePct - cfg.ChargeTargetPct) < 0.01
                ? cf : null;
            var chargeFresh = activeCharge is not null && now - activeCharge.Generated < FreshFor
                && s.LevelPct is double cl && Math.Abs(cl - activeCharge.Level) <= FreshDrift;
            var target = TargetTime.Next(now, cfg.TargetTime, zone);
            var (saving, savingAt) = Suggest(active, now, target.When, s.BatterySaver == true);
            next = new DashboardState
            {
                Now = now,
                Battery = s,
                SystemContext = collector.CurrentContext,
                Phase = PhaseOf(s),
                Forecast = active,
                ForecastFresh = fresh,
                Recent = s.IsDischarging ? recent : null,
                Charging = charging,
                ChargeForecast = activeCharge,
                ChargeForecastFresh = chargeFresh,
                AdditionalTargets = [.. cfg.AdditionalTargets.Select(t => TargetTime.Next(now, t, zone))],
                Updating = updating,
                Engine = engineState,
                EngineError = engineError,
                Target = target,
                AtTarget = active?.At(target.When),
                Saving = saving,
                SavingAt = savingAt,
                ReservePct = cfg.ReservePct,
                Demo = demo,
            };
            state = next;
        }
        Changed?.Invoke(next);
    }

    RecentEstimate? SafeRecent(BatterySample s, double reserve, DateTimeOffset now)
    {
        if (!s.IsDischarging) return null;
        try { return RecentRate.Estimate([.. store.Samples(now - RecentLookback, now), s], reserve); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return RecentRate.Estimate([s], reserve); }
    }

    internal static PowerPhase PhaseOf(BatterySample s) => s switch
    {
        { HasBattery: false } => PowerPhase.NoBattery,
        { LevelPct: null } or { OnAc: null } => PowerPhase.SensorUnavailable,
        { OnAc: false } => PowerPhase.Discharging,
        { State: ChargeState.Charging } => PowerPhase.Charging,
        _ => PowerPhase.PluggedIn,
    };

    internal static (SavingSuggestion, DateTimeOffset?) Suggest(ForecastView? f, DateTimeOffset now, DateTimeOffset target, bool saverOn)
    {
        if (saverOn || f is null) return (SavingSuggestion.None, null);
        if (f.Central is { } c && c > now && c < target) return (SavingSuggestion.Likely, c);
        if (f.Early is { } e && e > now && e < target) return (SavingSuggestion.Possible, e);
        return (SavingSuggestion.None, null);
    }

    ForecastView? LoadCache()
    {
        if (cachePath is null) return null;
        try
        {
            var r = JsonSerializer.Deserialize<ForecastResponse>(File.ReadAllText(cachePath), Protocol.Json);
            return r is null ? null : ForecastView.From(r);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    void SaveCache(ForecastResponse r)
    {
        if (cachePath is null) return;
        try { AppPaths.WriteAtomic(cachePath, JsonSerializer.Serialize(r, Protocol.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => collector.Sampled -= OnSampled;
}
