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
    /// <summary>
    /// Collegato ma con la carica ferma (limite di carica, batteria piena): previsione Timestral "se stacchi adesso",
    /// calcolata dal livello attuale. Null se manca o se il livello si è mosso troppo da quando è stata calcolata.
    /// </summary>
    public ForecastView? HeldForecast { get; init; }
    public bool HeldForecastFresh { get; init; }
    public IReadOnlyList<TargetTime> AdditionalTargets { get; init; } = [];
    public bool Updating { get; init; }
    public EngineState Engine { get; init; }
    public string? EngineError { get; init; }
    public bool EveningTargetEnabled { get; init; } = true;
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

    /// <summary>
    /// Quale previsione: a batteria fino alla riserva; in carica fino all'obiettivo; collegato con la carica ferma,
    /// "se stacchi adesso" (scenario di scarica dal livello attuale). Quest'ultima non parte mai dal timer: il livello
    /// è fermo e il PC può restare collegato tutto il giorno, si ricalcola all'apertura del pannello o a un evento.
    /// </summary>
    enum Mode { Discharge, Charge, Held }

    /// <summary>Oltre questo scarto di livello la previsione "se stacchi adesso" non descrive più la batteria.</summary>
    const double HeldMaxDrift = 3;

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
    readonly Action<Action> queueManualWork;
    readonly Lock gate = new();

    ForecastView? forecast;
    DateTimeOffset? lastRun, lastAcSeen, lastChargeRun, lastBatterySeen;
    ForecastView? chargeForecast, heldForecast;
    DateTimeOffset? lastHeldRun;
    bool updating, pendingForce, manualRead;
    bool pendingEvent;
    long operatingEpoch,inFlightEpoch;
    EngineState engineState = EngineState.Off;
    string? engineError;
    DashboardState? state;

    public ForecastCoordinator(Collector collector, HistoryStore store, IForecastEngine? engine, Func<Settings> settings,
        string? cachePath = null, TimeProvider? clock = null, TimeZoneInfo? zone = null, bool demo = false,
        Action<Action>? queueManualWork = null)
    {
        this.collector = collector;
        this.store = store;
        this.engine = engine;
        this.settings = settings;
        this.cachePath = cachePath;
        this.clock = clock ?? TimeProvider.System;
        this.zone = zone ?? TimeZoneInfo.Local;
        this.demo = demo;
        this.queueManualWork = queueManualWork ?? (work => { _ = Task.Run(work); });
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
        collector.Interrupted += OnInterrupted;
        collector.Start();          // prima lettura: pubblica lo stato e, a batteria, avvia il motore e la previsione
    }

    /// <summary>All'apertura del pannello i dati devono essere freschi: altrimenti parte il ricalcolo.</summary>
    public void PanelOpened() => Task.Run(() =>
    {
        collector.Sample();
        bool fresh;
        lock (gate) fresh = state switch
        {
            { Phase: PowerPhase.Charging } => state.ChargeForecastFresh,
            { Phase: PowerPhase.PluggedIn } => state.HeldForecastFresh,
            _ => state?.ForecastFresh == true,
        };
        if (!fresh) Request(Trigger.User);
    });

    public void Resumed() => Task.Run(() =>
    {
        if(collector.OnResume()) Request(Trigger.Event);
    });

    /// <summary>Le preferenze sono cambiate (riserva, obiettivo): la cache con un'altra riserva non vale più.</summary>
    public void SettingsChanged(bool recalculate = true) => Task.Run(() =>
    {
        Publish();
        if (recalculate) Request(Trigger.User);
    });

    public void Retry() => RecalculateNow();
    public void CalibrationChanged()=>Task.Run(()=> { OnInterrupted("calibration",clock.GetUtcNow());Request(Trigger.User); });

    /// <summary>Fresh readings and an explicit local forecast, including in battery saver/on-open mode.</summary>
    public void RecalculateNow()
    {
        // Claim the click before scheduling: a busy thread pool must not retain clicks for later.
        lock (gate)
        {
            if (updating || manualRead) return;
            manualRead = true;
        }
        queueManualWork(() =>
        {
            try
            {
                collector.Sample(forceContext: true);
                Request(Trigger.User);
            }
            finally { lock (gate) manualRead = false; }
        });
    }

    /// <summary>Lo storico è stato cancellato: si dimentica anche la previsione che ne derivava.</summary>
    public void Reset() => Task.Run(() =>
    {
        lock (gate)
        {
            operatingEpoch++;
            (forecast, lastRun, lastAcSeen, chargeForecast, lastChargeRun, heldForecast, lastHeldRun) = (null, null, null, null, null, null, null);
        }
        Publish();
        Request(Trigger.User);
    });

    void OnSampled(BatterySample s, PowerChange change)
    {
        if(change!=PowerChange.None) OnInterrupted("adapter",s.TimestampUtc);
        if (s.OnAc == true)
            lock (gate) lastAcSeen = s.TimestampUtc;
        else if (s.OnAc == false)
            lock (gate) lastBatterySeen = s.TimestampUtc;
        Publish();
        lock (gate) if (manualRead) return;
        Request(change == PowerChange.None ? Trigger.Background : Trigger.Event);
    }

    void OnInterrupted(string kind,DateTimeOffset at)
    {
        lock(gate)
        {
            operatingEpoch++;
            forecast=chargeForecast=heldForecast=null;
            lastRun=lastChargeRun=lastHeldRun=null;
            pendingEvent|=updating;
            if(cachePath is not null)
                try { File.Delete(cachePath); } catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        }
        Publish();
    }

    void SetEngine(EngineState s, string? error)
    {
        lock (gate) (engineState, engineError) = (s, error);
        Publish();
    }

    void Request(Trigger trigger)
    {
        var s = collector.Current;
        if (engine is null || collector.IsSuspended || s is not { LevelPct: double level }) return;
        // in scarica la previsione fino alla riserva; in carica, finché l'obiettivo non è raggiunto, quella di carica;
        // collegato con la carica ferma, quella "se stacchi adesso"
        Mode mode;
        if (s.IsDischarging) mode = Mode.Discharge;
        else if (s is { OnAc: true, State: ChargeState.Charging } && (level < settings().ChargeTargetPct || trigger == Trigger.User)) mode = Mode.Charge;
        else if (PhaseOf(s) == PowerPhase.PluggedIn && trigger != Trigger.Background) mode = Mode.Held;
        else return;
        var now = clock.GetUtcNow();
        var auto = Automatic(settings().Refresh, s.BatterySaver == true);
        if (trigger != Trigger.User && auto is null) return;       // solo su richiesta dell'utente
        var force = trigger != Trigger.Background;
        long requestEpoch;
        lock (gate)
        {
            if (updating)
            {
                pendingForce |= trigger == Trigger.User;
                pendingEvent |= trigger==Trigger.Event && inFlightEpoch!=operatingEpoch;
                return;
            }
            if (!force)
            {
                var age = (mode == Mode.Charge ? lastChargeRun : lastRun) is { } r ? now - r : TimeSpan.MaxValue;
                var drift = (mode == Mode.Charge ? chargeForecast : forecast) is { } f ? Math.Abs(level - f.Level) : 99;
                if (!(age >= auto!.Value.Every || (drift >= auto.Value.Drift && age >= DriftMinAge))) return;
            }
            updating = true;
            requestEpoch=inFlightEpoch=operatingEpoch;
            switch (mode)
            {
                case Mode.Charge: lastChargeRun = now; break;
                case Mode.Held: lastHeldRun = now; break;
                default: lastRun = now; break;
            }
        }
        Publish();
        _ = RunAsync(s, level, now, mode,requestEpoch);
    }

    async Task RunAsync(BatterySample s, double level, DateTimeOffset started, Mode mode,long requestEpoch)
    {
        var charge = mode == Mode.Charge;
        var options = settings();
        // All fresh measurements are available by this cutoff; the battery anchor may precede it by milliseconds.
        var capturedContext=collector.CurrentContext;
        var origin = capturedContext is { } context && context.TimestampUtc>s.TimestampUtc && context.TimestampUtc<=clock.GetUtcNow()
            ? context.TimestampUtc : s.TimestampUtc;
        var reserve = charge ? options.ChargeTargetPct : options.ReservePct;
        try
        {
            if (!engine!.IsWarm) SetEngine(EngineState.Loading, null);
            var history = store.ModelSamples(origin - HistoryDays, origin);
            var modelContext=options.UseSystemContext && options.CollectSystemContext
                ? store.ModelCovariates(origin-HistoryDays,origin,currentBatteryOnly:true,includeApps:options.CollectProcesses) : null;
            if(modelContext is not null && !options.CollectProcesses)
                foreach(var key in new[]{"process_cpu_pct","process_memory_mb","workload_profile"}) modelContext.Remove(key);
            var r = await engine.ForecastAsync(new ForecastRequest(origin, level, s.OnAc, reserve, history,
                modelContext,
                Charge: charge, TargetPct: reserve,
                Discontinuities:store.ModelDiscontinuities(origin-HistoryDays,origin))).ConfigureAwait(false);
            var useContext=options.UseSystemContext && options.CollectSystemContext;
            var conditions=ForecastConditions.Capture(s,useContext?capturedContext:null,useContext&&options.CollectProcesses);
            r=r with { Conditions=conditions };
            var report=new CalibrationInfo { Enabled=options.UseLocalCalibration && !demo,Reason=demo?"demo":"disabled",ResetUtc=options.CalibrationResetUtc };
            if(options.UseLocalCalibration && !demo && r.TimeBasis=="active_use" && r.Source=="timestral")
            {
                var since=origin.AddDays(-Math.Min(30,options.RetentionDays));
                if(options.CalibrationResetUtc is { } reset && reset>since) since=reset;
                if(since<origin)
                    report=LocalCalibration.Learn(r,store.CalibrationSnapshots(since,origin,charge),store.Samples(since,origin),
                        store.ModelDiscontinuities(since,origin),
                        useContext?store.CalibrationContext(since,origin,options.CollectProcesses):[],origin,options.CalibrationResetUtc);
                else report=report with { Reason="collecting" };
            }
            r=LocalCalibration.Apply(r,report);
            var view = ForecastView.From(r);
            lock (gate)
            {
                if(requestEpoch!=operatingEpoch) view=null;
                var latest=settings();
                if(latest.UseLocalCalibration!=options.UseLocalCalibration || latest.CalibrationResetUtc!=options.CalibrationResetUtc ||
                    latest.UseSystemContext!=options.UseSystemContext || latest.CollectSystemContext!=options.CollectSystemContext || latest.CollectProcesses!=options.CollectProcesses)
                { view=null;pendingForce=true; }
                // Cambiata l'alimentazione mentre si calcolava: la risposta descrive uno scenario che non c'è più.
                if (mode == Mode.Discharge && lastAcSeen is { } ac && ac >= started) view = null;
                if (charge && lastBatterySeen is { } bat && bat >= started) view = null;
                if (view is not null)
                    switch (mode)
                    {
                        case Mode.Charge: chargeForecast = view; break;
                        case Mode.Held: heldForecast = view; break;
                        default: forecast = view; break;
                    }
                (engineState, engineError) = (EngineState.Ready, null);
            }
            if (view is not null)
            {
                lock(gate) if (mode == Mode.Discharge && requestEpoch==operatingEpoch) SaveCache(r);
                try
                {
                    var source = mode switch { Mode.Charge => r.Source + ":charge", Mode.Held => r.Source + ":held", _ => r.Source };
                    store.RecordForecast(view.Generated, source, level, reserve, r.Crossing.Status, view.Central,
                        view.Early, view.Late, r.Quality.State, r.ModelId, r.ModelRevision, r.ElapsedMs, snapshot: r, issuedAt: clock.GetUtcNow());
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
        bool again,eventAgain;
        lock (gate)
        {
            updating = false;
            again = pendingForce;
            pendingForce = false;
            eventAgain=pendingEvent;pendingEvent=false;
        }
        Publish();
        if (again) Request(Trigger.User);
        else if(eventAgain) Request(Trigger.Event);
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
            var activeHeld = heldForecast is { } hf && s.OnAc == true && now - hf.Generated < ValidFor
                && Math.Abs(hf.ReservePct - cfg.ReservePct) < 0.01 && s.LevelPct is double hl && Math.Abs(hl - hf.Level) <= HeldMaxDrift
                ? hf : null;
            var heldFresh = activeHeld is not null && now - activeHeld.Generated < FreshFor
                && Math.Abs(s.LevelPct!.Value - activeHeld.Level) <= FreshDrift;
            var target = TargetTime.Next(now, cfg.TargetTime, zone);
            var (saving, savingAt) = cfg.EveningTargetEnabled
                ? Suggest(active, now, target.When, s.BatterySaver == true)
                : (SavingSuggestion.None, (DateTimeOffset?)null);
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
                HeldForecast = activeHeld,
                HeldForecastFresh = heldFresh,
                EveningTargetEnabled = cfg.EveningTargetEnabled,
                AdditionalTargets = cfg.EveningTargetEnabled ? [.. cfg.AdditionalTargets.Select(t => TargetTime.Next(now, t, zone))] : [],
                Updating = updating,
                Engine = engineState,
                EngineError = engineError,
                Target = target,
                AtTarget = cfg.EveningTargetEnabled ? active?.At(target.When) : null,
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
        if (!s.IsDischarging || collector.IsSuspended || now-s.TimestampUtc>TimeSpan.FromMinutes(2)) return null;
        try
        {
            var since=now-RecentLookback;
            var breaks=store.ModelDiscontinuities(since,now);
            if(breaks.Count>0) since=breaks[^1];
            return RecentRate.Estimate([.. store.Samples(since, now), s], reserve);
        }
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
            if (r is null) return null;
            if (r.Curve.Count > 0 && double.IsFinite(r.GeneratedAtUtc) && r.GeneratedAtUtc >= r.ContextEndUtc &&
                r.GeneratedAtUtc >= clock.GetUtcNow().AddDays(-settings().RetentionDays).ToUnixTimeSeconds() &&
                r.GeneratedAtUtc <= clock.GetUtcNow().ToUnixTimeSeconds())
            {
                try { store.PreserveCachedForecast(r, DateTimeOffset.FromUnixTimeMilliseconds((long)(r.GeneratedAtUtc * 1000))); }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
            }
            // Preserve the genuine legacy curve above, but never reuse its old time basis as a new estimate.
            if(r.Source=="timestral" && r.TimeBasis!="active_use") return null;
            if(r.Calibration is { } calibration && (calibration.Enabled!=settings().UseLocalCalibration || calibration.ResetUtc!=settings().CalibrationResetUtc)) return null;
            return ForecastView.From(r);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    void SaveCache(ForecastResponse r)
    {
        if (cachePath is null) return;
        try { AppPaths.WriteAtomic(cachePath, JsonSerializer.Serialize(r, Protocol.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() { collector.Sampled -= OnSampled;collector.Interrupted-=OnInterrupted; }
}
