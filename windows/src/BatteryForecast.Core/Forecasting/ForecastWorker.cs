using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BatteryForecast.Core.Forecasting;

/// <summary>Come avviare il motore: eseguibile PyInstaller nella release, python del venv in sviluppo.</summary>
public sealed record WorkerLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record WorkerOptions
{
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Senza richieste per questo tempo il motore si ferma e libera la memoria (come l'originale).</summary>
    public TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed class ForecastWorkerException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Processo persistente di Timestral: si avvia una volta (carica PyTorch e il modello) e poi risponde a
/// una richiesta alla volta. Ogni avvio è una "sessione": le risposte di una sessione chiusa non possono
/// arrivare a chi aspetta nella successiva. Thread-safe; nessun lavoro sul thread della UI.
/// </summary>
public interface IForecastEngine
{
    WorkerReady? Info { get; }
    bool IsWarm { get; }
    Task<ForecastResponse> ForecastAsync(ForecastRequest request, CancellationToken ct = default);
    Task WarmUpAsync(CancellationToken ct = default);
    Task StopAsync();
}

public sealed class ForecastWorker : IForecastEngine, IAsyncDisposable
{
    readonly WorkerLaunch launch;
    readonly WorkerOptions options;
    readonly TimeProvider clock;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Lazy<JobObject?> job = new(() => OperatingSystem.IsWindows() ? new JobObject() : null);
    Session? session;
    ITimer? idleTimer;
    long nextId = 1;
    int failures;
    DateTimeOffset notBefore = DateTimeOffset.MinValue;
    bool disposed;

    public ForecastWorker(WorkerLaunch launch, WorkerOptions? options = null, TimeProvider? clock = null)
    {
        this.launch = launch;
        this.options = options ?? new();
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Modello caricato dall'ultima sessione pronta.</summary>
    public WorkerReady? Info { get; private set; }
    public bool IsWarm => session is { IsReady: true, HasExited: false };
    /// <summary>Ultime righe di stderr del motore, per la diagnostica.</summary>
    public string StderrTail => session?.StderrTail ?? lastTail;
    string lastTail = "";

    public async Task<ForecastResponse> ForecastAsync(ForecastRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = await ReadySessionAsync(ct).ConfigureAwait(false);
            var id = Interlocked.Increment(ref nextId);
            var reply = await s.SendAsync(id, Protocol.EncodeRequest(id, request), options.RequestTimeout, ct)
                .ConfigureAwait(false);
            ArmIdle();
            switch (reply)
            {
                case WorkerForecast f:
                    failures = 0;
                    return f.Response;
                case WorkerError e:
                    throw new ForecastWorkerException(e.Code, e.Message, e.Retryable);
                default:
                    throw new ForecastWorkerException("bad_reply", $"unexpected reply {reply.GetType().Name}", true);
            }
        }
        catch (ForecastWorkerException e) when (e.Code is "timeout" or "exited")
        {
            Fail();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Avvia il motore senza fare richieste, così è pronto quando serve.</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReadySessionAsync(ct).ConfigureAwait(false);
            ArmIdle();
        }
        catch (ForecastWorkerException e) when (e.Code is "timeout" or "exited")
        {
            Fail();
            throw;
        }
        finally { gate.Release(); }
    }

    async Task<Session> ReadySessionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (session is { HasExited: false } live)
            return await live.WaitReadyAsync(options.LoadTimeout, ct).ConfigureAwait(false);
        if (clock.GetUtcNow() < notBefore)
            throw new ForecastWorkerException("backoff", $"engine restart paused until {notBefore:HH:mm:ss}", true);
        var s = Session.Start(launch, job.Value);
        session = s;
        var ready = await s.WaitReadyAsync(options.LoadTimeout, ct).ConfigureAwait(false);
        Info = ready.Info;
        return ready;
    }

    void Fail()
    {
        failures++;
        var delay = options.FirstBackoff * Math.Pow(2, Math.Min(failures - 1, 16));
        notBefore = clock.GetUtcNow() + (delay < options.MaxBackoff ? delay : options.MaxBackoff);
        KillSession();
    }

    void ArmIdle()
    {
        idleTimer?.Dispose();
        idleTimer = clock.CreateTimer(_ => _ = StopIfIdleAsync(), null, options.IdleLimit, Timeout.InfiniteTimeSpan);
    }

    async Task StopIfIdleAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return;     // una richiesta è in corso: resta acceso
        try { await StopSessionAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopSessionAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    async Task StopSessionAsync()
    {
        idleTimer?.Dispose();
        idleTimer = null;
        if (session is not { } s) return;
        session = null;
        lastTail = s.StderrTail;
        await s.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    void KillSession()
    {
        if (session is not { } s) return;
        session = null;
        lastTail = s.StderrTail;
        s.Kill();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAsync().ConfigureAwait(false);
        disposed = true;
        if (job.IsValueCreated) job.Value?.Dispose();
        gate.Dispose();
    }

    /// <summary>Un processo avviato, con le sue richieste in attesa.</summary>
    sealed class Session
    {
        readonly Process process;
        readonly StreamWriter stdin;
        readonly TaskCompletionSource<WorkerReady> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ConcurrentDictionary<long, TaskCompletionSource<WorkerMessage>> pending = new();
        readonly Queue<string> stderr = new();
        const int StderrLines = 40, StderrLineChars = 400;

        public WorkerReady? Info { get; private set; }
        public bool IsReady => ready.Task.IsCompletedSuccessfully;
        public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }

        public string StderrTail { get { lock (stderr) return string.Join('\n', stderr); } }

        Session(Process p)
        {
            process = p;
            stdin = p.StandardInput;
            stdin.AutoFlush = false;
            stdin.NewLine = "\n";
        }

        public static Session Start(WorkerLaunch launch, JobObject? job)
        {
            var psi = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = launch.WorkingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // senza BOM: un BOM in testa renderebbe invalida la prima riga JSON
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var a in launch.Arguments) psi.ArgumentList.Add(a);
            // Il motore non deve leggere pacchetti dell'utente né scrivere .pyc nella cartella di installazione.
            psi.Environment["PYTHONNOUSERSITE"] = "1";
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["HF_HUB_OFFLINE"] = "1";
            // I thread OpenMP di PyTorch altrimenti restano in attesa attiva dopo il calcolo: CPU senza motivo.
            psi.Environment["OMP_WAIT_POLICY"] = "PASSIVE";
            psi.Environment["KMP_BLOCKTIME"] = "0";
            psi.Environment.Remove("PYTHONHOME");
            psi.Environment.Remove("PYTHONSTARTUP");
            if (launch.Environment is { } env)
                foreach (var (k, v) in env) psi.Environment[k] = v;

            Process p;
            try { p = Process.Start(psi) ?? throw new InvalidOperationException("process did not start"); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new ForecastWorkerException("not_found", $"cannot start engine {launch.FileName}: {e.Message}", false);
            }
            try { job?.Add(p); }
            catch (System.ComponentModel.Win32Exception) { /* processo già in un job non annidabile: resta EOF/Kill */ }
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }

            var s = new Session(p);
            p.ErrorDataReceived += (_, e) => s.OnStderr(e.Data);
            p.BeginErrorReadLine();
            _ = Task.Run(s.ReadLoopAsync);
            return s;
        }

        void OnStderr(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (stderr)
            {
                stderr.Enqueue(line.Length > StderrLineChars ? line[..StderrLineChars] + "…" : line);
                while (stderr.Count > StderrLines) stderr.Dequeue();
            }
        }

        async Task ReadLoopAsync()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    switch (Protocol.Decode(line))
                    {
                        case WorkerReady r:
                            Info = r;
                            ready.TrySetResult(r);
                            break;
                        case WorkerError { Id: null } e when !ready.Task.IsCompleted:
                            ready.TrySetException(new ForecastWorkerException(e.Code, e.Message, e.Retryable));
                            break;
                        case { Id: long id } m when pending.TryRemove(id, out var tcs):
                            tcs.TrySetResult(m);
                            break;
                        // errore su una richiesta di cui il motore non ha letto l'id: le richieste sono una alla
                        // volta, quindi è la risposta a quella in attesa (altrimenti resterebbe appesa fino al timeout)
                        case WorkerError { Id: null } e:
                            foreach (var key in pending.Keys)
                                if (pending.TryRemove(key, out var waiting)) waiting.TrySetResult(e);
                            break;
                    }
                }
            }
            catch (Exception) { /* pipe chiusa durante la lettura: si tratta come uscita */ }
            var tail = StderrTail.Split('\n').LastOrDefault(l => !l.Contains("Warning", StringComparison.Ordinal)) ?? "";
            var ex = new ForecastWorkerException("exited", $"engine stopped{(tail.Length > 0 ? ": " + tail : "")}", true);
            ready.TrySetException(ex);
            foreach (var id in pending.Keys)
                if (pending.TryRemove(id, out var tcs)) tcs.TrySetException(ex);
        }

        public async Task<Session> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            try { await ready.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                throw new ForecastWorkerException("timeout", $"engine not ready after {timeout.TotalSeconds:0} s", true);
            }
            return this;
        }

        public async Task<WorkerMessage> SendAsync(long id, string line, TimeSpan timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            try
            {
                await stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await stdin.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                pending.TryRemove(id, out _);
                throw new ForecastWorkerException("exited", "engine input closed", true);
            }
            try { return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                throw new ForecastWorkerException("timeout", $"no forecast after {timeout.TotalSeconds:0} s", true);
            }
            finally { pending.TryRemove(id, out _); }
        }

        /// <summary>Chiude stdin (il motore esce da solo), poi termina il processo se non è uscito.</summary>
        public async Task StopAsync(TimeSpan grace)
        {
            try { stdin.Close(); } catch (IOException) { }
            using var cts = new CancellationTokenSource(grace);
            try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Kill(); }
            process.Dispose();
        }

        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
