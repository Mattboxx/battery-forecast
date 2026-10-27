using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Storage;

/// <summary>
/// Legge la batteria a intervalli regolari e su evento, scrive lo storico e registra gli eventi di
/// alimentazione. Non tiene sveglio il PC: in sospensione il timer non scatta e la lacuna resta tale.
/// Le letture avvengono sul pool di thread, mai sul thread della UI.
/// </summary>
public sealed class Collector : IDisposable
{
    readonly IBatteryProvider provider;
    readonly HistoryStore store;
    readonly TimeProvider clock;
    readonly TimeSpan interval;
    readonly ISystemContextProvider? context;
    readonly Func<bool> collectContext;
    readonly Func<int> contextIntervalSeconds;
    DateTimeOffset? lastContextRead;
    public SystemSample? CurrentContext { get; private set; }
    readonly Lock gate = new();
    ITimer? timer, contextTimer;
    bool? lastOnAc;
    bool disposed;
    bool suspended;
    readonly SamplingContinuity continuity=new();
    readonly Func<OperatingClockReading?> operatingClock;
    DateTimeOffset? lastResume;
    double? lastResumeBias;

    public Collector(IBatteryProvider provider, HistoryStore store, TimeProvider? clock = null, TimeSpan? interval = null, ISystemContextProvider? context = null, Func<bool>? collectContext = null, Func<int>? contextIntervalSeconds = null, Func<OperatingClockReading?>? operatingClock = null)
    {
        this.contextIntervalSeconds = contextIntervalSeconds ?? (() => 60);
        this.context = context;
        this.collectContext = collectContext ?? (() => true);
        this.provider = provider;
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
        this.operatingClock=operatingClock ?? (ReferenceEquals(this.clock,TimeProvider.System) ? OperatingClock.Read : () => null);
        this.interval = interval ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Ultima lettura (anche se non salvata: senza batteria non si scrive nulla).</summary>
    public BatterySample? Current { get; private set; }

    /// <summary>Dopo ogni lettura; l'argomento dice se l'alimentatore è cambiato rispetto alla precedente.</summary>
    public event Action<BatterySample, PowerChange>? Sampled;
    public event Action<string,DateTimeOffset>? Interrupted;
    public bool IsSuspended { get { lock(gate) return suspended; } }
    public void ResetContextIntervals() { lock(gate) { context?.ResetIntervals();lastContextRead=null; } }

    public void Start()
    {
        store.AppendEvent(clock.GetUtcNow(), "app_start");
        Sample(collectionKind: "startup");
        timer = clock.CreateTimer(_ => Sample(collectionKind: "scheduled"), null, interval, interval);
        if (context is not null)
            contextTimer = clock.CreateTimer(_ => ContextTick(), null, ContextInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Lettura immediata: cambio di alimentazione, ripresa, apertura del pannello.</summary>
    public BatterySample Sample(bool forceContext = false, string collectionKind = "diagnostic")
    {
        BatterySample s;
        PowerChange change;
        SamplingBreak? interruption;
        lock (gate)
        {
            if (disposed) return Current ?? throw new ObjectDisposedException(nameof(Collector));
            var now=clock.GetUtcNow();var os=operatingClock();
            interruption=continuity.Observe(now,os);
            if(suspended && interruption is null) return Current ?? throw new InvalidOperationException("Suspended before first sample");
            if(interruption is { } boundary)
            {
                suspended=false;context?.ResetIntervals();lastContextRead=null;CurrentContext=null;
                TryEventAt(boundary.From,"sampling_gap_start");TryEventAt(boundary.Until,boundary.Kind);
                lastResume=now;lastResumeBias=os is { } time ? time.ElapsedSeconds-time.AwakeSeconds : null;
            }
            s = provider.Read();
            Current = s;
            change = s.OnAc switch
            {
                true when lastOnAc == false => PowerChange.Connected,
                false when lastOnAc == true => PowerChange.Disconnected,
                _ => PowerChange.None,
            };
            if (s.OnAc is bool ac) lastOnAc = ac;
            try
            {
                store.Append(s,collectionKind);
                UpdateContext(s, forceContext, collectionKind);
                if (change != PowerChange.None)
                    store.AppendEvent(s.TimestampUtc, change == PowerChange.Connected ? "ac_connected" : "ac_disconnected");
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* disco pieno o file bloccato: la lettura resta valida */ }
        }
        if(interruption is { } broken) Interrupted?.Invoke(broken.Kind,s.TimestampUtc);
        Sampled?.Invoke(s, change);
        return s;
    }

    TimeSpan ContextInterval => TimeSpan.FromSeconds(Math.Clamp(contextIntervalSeconds(), 30, 120));

    void UpdateContext(BatterySample? battery = null, bool force = false, string collectionKind = "scheduled")
    {
        if (context is null || !collectContext())
        {
            CurrentContext = null;
            lastContextRead = null;
            context?.ResetIntervals();
            return;
        }
        var now = clock.GetUtcNow();
        // A small tolerance avoids losing an entire sample to ordinary timer jitter.
        if (!force && lastContextRead is { } at && now - at + TimeSpan.FromMilliseconds(250) < ContextInterval) return;
        CurrentContext = context.Read(battery ?? provider.Read()) with { CollectionKind = collectionKind };
        lastContextRead = now;
        contextTimer?.Change(ContextInterval, Timeout.InfiniteTimeSpan);
        store.AppendContext(CurrentContext);
    }

    void ContextTick()
    {
        lock (gate)
        {
            if (disposed || suspended) return;
            try { UpdateContext(); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            finally { contextTimer?.Change(ContextInterval, Timeout.InfiniteTimeSpan); }
        }
    }

    public void OnDisplayStateChanged(int state)
    {
        lock (gate)
        {
            if (disposed || suspended || !collectContext() || state is < 0 or > 2) return;
            try
            {
                store.AppendEvent(clock.GetUtcNow(), "display_" + state);
                UpdateContext(force: true,collectionKind: "display");
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
    }

    public void OnSuspend()
    {
        DateTimeOffset now;
        lock(gate)
        {
            if(disposed || suspended) return;
            suspended=true;context?.ResetIntervals();CurrentContext=null;now=clock.GetUtcNow();TryEventAt(now,"suspend");
        }
        Interrupted?.Invoke("suspend",now);
    }

    public bool OnResume()
    {
        DateTimeOffset now;
        lock(gate)
        {
            if(disposed) return false;
            now=clock.GetUtcNow();var os=operatingClock();
            var bias=os is { } t ? t.ElapsedSeconds-t.AwakeSeconds : (double?)null;
            if(!suspended && lastResume is { } at && (bias is { } b && lastResumeBias is { } prior ? Math.Abs(b-prior)<1 : now-at<TimeSpan.FromSeconds(5))) return false;
            suspended=false;lastResume=now;lastResumeBias=bias;continuity.Reset();
            context?.ResetIntervals();lastContextRead=null;CurrentContext=null;TryEventAt(now,"resume");
        }
        Interrupted?.Invoke("resume",now);
        Sample(forceContext:true,collectionKind: "resume");
        return true;
    }

    public void OnExit() => TryEvent("app_exit");

    void TryEvent(string kind)
    {
        TryEventAt(clock.GetUtcNow(),kind);
    }

    void TryEventAt(DateTimeOffset at,string kind)
    {
        try { store.AppendEvent(at, kind); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            contextTimer?.Dispose();
        }
    }
}

public enum PowerChange { None, Connected, Disconnected }
