using Microsoft.Win32;

namespace BatteryForecast.App;

/// <summary>
/// Un'istanza per sessione utente (nomi "Local\"). Una seconda apertura segnala la prima, che mostra
/// il pannello, ed esce. Con <c>--quit</c> chiede invece all'istanza attiva di chiudersi (installer).
/// </summary>
sealed class SingleInstance : IDisposable
{
    readonly Mutex mutex;
    readonly EventWaitHandle show, quit;
    readonly RegisteredWaitHandle showWait, quitWait;

    SingleInstance(Mutex mutex, EventWaitHandle show, EventWaitHandle quit, Action onShow, Action onQuit)
    {
        this.mutex = mutex;
        this.show = show;
        this.quit = quit;
        showWait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => onShow(), null, Timeout.Infinite, false);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => onQuit(), null, Timeout.Infinite, true);
    }

    /// <summary>Null se un'altra istanza è già attiva (e in quel caso le chiede di mostrarsi).</summary>
    public static SingleInstance? TryAcquire(string name, Action onShow, Action onQuit)
    {
        var mutex = new Mutex(true, $@"Local\{name}.Instance", out var created);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Quit");
        if (!created)
        {
            show.Set();
            show.Dispose();
            quit.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, show, quit, onShow, onQuit);
    }

    /// <summary>Chiede all'istanza attiva di uscire e attende fino a <paramref name="timeout"/>. False se non è uscita.</summary>
    public static bool RequestQuit(string name, TimeSpan timeout)
    {
        if (!EventWaitHandle.TryOpenExisting($@"Local\{name}.Quit", out var quit)) return true;   // nessuna istanza
        using (quit) quit.Set();
        if (!Mutex.TryOpenExisting($@"Local\{name}.Instance", out var m)) return true;
        using (m)
        {
            try
            {
                if (!m.WaitOne(timeout)) return false;
                m.ReleaseMutex();
            }
            catch (AbandonedMutexException) { }
            return true;
        }
    }

    public void Dispose()
    {
        showWait.Unregister(null);
        quitWait.Unregister(null);
        show.Dispose();
        quit.Dispose();
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose();
    }
}

/// <summary>Avvio all'accesso per l'utente corrente (HKCU\...\Run): nessun privilegio amministrativo.</summary>
static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "BatteryForecast";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>La voce del vecchio nome (Till Dusk) avvierebbe un eseguibile che non c'è più: si sostituisce.</summary>
    public static void MigrateLegacy()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k?.GetValue("TillDusk") is not string) return;
        k.DeleteValue("TillDusk", throwOnMissingValue: false);
        Set(true);
    }
}
