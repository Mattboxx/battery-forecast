using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Notifications;
using BatteryForecast.Core.Presentation;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Updates;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.App;

/// <summary>
/// App residente: nessuna finestra all'avvio, solo l'icona. Il pannello si apre al clic; "Esci" ferma
/// timer, scritture, icona e motore.
///
///   BatteryForecast.exe               avvio normale (al primo avvio mostra il pannello)
///   BatteryForecast.exe --background  avvio all'accesso, senza pannello
///   BatteryForecast.exe --demo        dati sintetici dichiarati, in una cartella temporanea separata
///   BatteryForecast.exe --snapshot D  demo, attende la previsione, salva in D le immagini del pannello ed esce
/// </summary>
public partial class App : Application
{
    SingleInstance? instance;
    TrayIcon? tray;
    PanelWindow? panel;
    PreferencesWindow? prefs;
    HistoryWindow? history;
    Collector? collector;
    ForecastWorker? worker;
    AppPaths paths = AppPaths.Default;
    DashboardState? last;
    NotificationRules? notices;
    readonly SemaphoreSlim noticeGate = new(1, 1);
    bool demo, quitting;
    public bool UiTest { get; private set; }
    Log? log;
    GlobalHotkey? hotkey;
    public string? ShortcutError { get; private set; }

    public const string SupportUrl = "https://ko-fi.com/mattboxx";

    /// <summary>Versione dell'app (InformationalVersion senza l'hash del commit).</summary>
    public string Version { get; } = (System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    UpdateService? updates;
    public UpdateService Updates => updates ??= new UpdateService(UpdateService.CreateClient(Version));
    public UpdateInfo? AvailableUpdate { get; private set; }
    DispatcherTimer? updateTimer;

    public HistoryStore? Store { get; private set; }
    public ForecastCoordinator? Coordinator { get; private set; }
    public SystemSample? CurrentSystemContext => collector?.CurrentContext;
    public Settings Settings { get; private set; } = new();

    public string EngineDescription => worker?.Info is { } i
        ? $"{i.ModelId} @ {i.ModelRevision[..Math.Min(7, i.ModelRevision.Length)]}" : "Timestral";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WheelScrolling.Register();
        var snapshotDir = e.Args.SkipWhile(a => a != "--snapshot").Skip(1).FirstOrDefault();
        demo = e.Args.Contains("--demo") || snapshotDir is not null;
#if DEBUG
        UiTest = demo && e.Args.Contains("--ui-test");
#endif
        var name = snapshotDir is not null ? "BatteryForecast.Snapshot" : demo ? "BatteryForecast.Demo" : "BatteryForecast";
        if (e.Args.Contains("--quit"))
        {   // usato dall'installer prima di aggiornare o disinstallare
            Shutdown(SingleInstance.RequestQuit(name, TimeSpan.FromSeconds(15)) ? 0 : 1);
            return;
        }
        instance = SingleInstance.TryAcquire(name, () => Dispatcher.BeginInvoke(ShowPanel), () => Dispatcher.BeginInvoke(Quit));
        if (instance is null) { Shutdown(); return; }
        CreateMessageWindow();

        paths = (demo ? new AppPaths(Path.Combine(Path.GetTempPath(), snapshotDir is not null ? "BatteryForecast-snapshot" : "BatteryForecast-demo")) : AppPaths.Default);
        if (demo && Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
        if (!demo)
        {
            AppPaths.MigrateLegacy(paths);
            try { LoginItem.MigrateLegacy(); } catch (UnauthorizedAccessException) { }
        }
        paths.Ensure();
        log = new Log(Path.Combine(paths.Logs, "app.log"));
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => log?.Write($"fatal: {a.ExceptionObject}");

        Settings = Settings.Load(paths.Settings);
        if (UiTest) Settings = Settings with { KeepPanelOpen = true, ShowChart = true, AutoUpdateCheck = false };
        // anche in demo, così la scorciatoia si può provare; non nelle istantanee
        if (snapshotDir is null && messages is not null)
        {
            hotkey = new GlobalHotkey(messages.Handle);
            if (HotkeySpec.TryParse(Settings.PanelHotkey, out var spec) && !hotkey.TrySet(spec)) ShortcutError = "HotkeyConflict";
        }
        // --lang it|en: solo per demo e istantanee (immagini del sito e del README)
        if (demo && e.Args.SkipWhile(a => a != "--lang").Skip(1).FirstOrDefault() is { } lang)
        {
            Settings = Settings with { Language = lang == "it" ? LanguageChoice.Italian : LanguageChoice.English };
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(lang == "it" ? "it-IT" : "en-GB");
        }
        L.Apply(Settings.Language);
        Theme.Apply(Settings.Theme);

        var now = DateTimeOffset.UtcNow;
        Store = new HistoryStore(paths.History);
        try { Store.Prune(now, Settings.RetentionDays); } catch (Microsoft.Data.Sqlite.SqliteException ex) { log.Write($"prune: {ex.Message}"); }
        IBatteryProvider provider = new WindowsBatteryProvider();
        if (demo)
        {
            // la demo parte dalla batteria vera (livello e capacità), poi simula la scarica
            var real = provider.Read();
            var start = Demo.StartFrom(real);
            int full = real.FullChargeMWh ?? 56_000, design = real.DesignMWh ?? Math.Max(full, 60_000);
            provider = new Demo.Provider(now, start, full, design);
            Demo.Seed(Store, now, TimeZoneInfo.Local, start, full, design);
        }

        var launch = EngineLocator.Find(AppContext.BaseDirectory);
        if (launch is null) log.Write("engine not found next to the app");
        // il motore si chiude un minuto dopo l'ultima previsione: non resta in memoria fra un calcolo e l'altro
        worker = launch is null ? null : new ForecastWorker(launch, new WorkerOptions { IdleLimit = TimeSpan.FromMinutes(1) });
        collector = new Collector(provider, Store, context: demo ? new Demo.ContextProvider() : new WindowsSystemContext(collectAdvanced: () => Settings.CollectAdvancedSensors, displayState: () => displayState.Value,collectProcesses:()=>Settings.CollectProcesses),
            collectContext: () => Settings.CollectSystemContext, contextIntervalSeconds: () => Settings.ContextIntervalSeconds);
        Coordinator = new ForecastCoordinator(collector, Store, worker, () => Settings, paths.LastForecast, demo: demo);
        Coordinator.Changed += s => Dispatcher.BeginInvoke(() => Render(s));
        notices = new NotificationRules(Store);
        if (!demo) StartUpdateTimer();

        tray = new TrayIcon(TogglePanel, ShowPanel, ShowHistory, ShowPreferences, Quit, L.T, OnTrayHover);
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.UserPreferenceChanged += OnUserPreference;
        SystemEvents.SessionEnding += (_, _) => Quit();

        var showPanel = snapshotDir is null && !e.Args.Contains("--background") && (demo || !Settings.OnboardingDone);
        Task.Run(() =>
        {
            Coordinator.Start();
            if (showPanel) Dispatcher.BeginInvoke(ShowPanel);
            if (snapshotDir is not null) _ = SnapshotAsync(snapshotDir);
        });
        if (!Settings.OnboardingDone && !demo) UpdateSettings(Settings with { OnboardingDone = true });
    }

    async Task SnapshotAsync(string dir)
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < until && Coordinator?.State is not { Forecast: not null, Updating: false })
            await Task.Delay(250);
        // per l'immagine "in carica": una vera previsione di carica di Timestral sullo stesso storico demo
        ForecastView? charge = null;
        if (worker is not null && Store is not null && Coordinator?.State is { } st)
            try
            {
                var r = await worker.ForecastAsync(new ForecastRequest(st.Now, Snapshots.ChargingLevel, true, Settings.ChargeTargetPct,
                    Store.ModelSamples(st.Now.AddDays(-110)), Charge: true, TargetPct: Settings.ChargeTargetPct));
                charge = ForecastView.From(r);
            }
            catch (ForecastWorkerException ex) { log?.Write($"snapshot charge: {ex.Message}"); }
        await Dispatcher.InvokeAsync(() =>
        {
            try { Snapshots.Save(this, Coordinator!.State!, dir, charge); }
            catch (Exception ex) { log?.Write($"snapshot: {ex}"); }
            Quit();
        });
    }

    HwndSource? messages;
    readonly DisplayStateTracker displayState = new();
    DisplayPowerNotification? displayNotification;

    /// <summary>
    /// Finestra invisibile di primo livello: Restart Manager (usato da Inno Setup) e "taskkill" senza /F
    /// chiudono le app inviando WM_CLOSE o WM_QUERYENDSESSION alle loro finestre. Senza, l'app resterebbe aperta.
    /// </summary>
    void CreateMessageWindow()
    {
        messages = new HwndSource(new HwndSourceParameters("Battery Forecast") { Width = 0, Height = 0, WindowStyle = 0 });
        displayNotification = new DisplayPowerNotification(displayState, state =>
        {
            if (!demo) _ = Task.Run(() => collector?.OnDisplayStateChanged(state));
#if DEBUG
            if (UiTest) Dispatcher.BeginInvoke(() => log?.Write("ui-test Windows display state=" + state));
#endif
        });
        messages.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            const int WM_CLOSE = 0x10, WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16;
            if (msg == 0x218 && w.ToInt32() == 0x8013) displayNotification?.OnMessage(l);
            if(msg==0x218 && !demo)
            {
                if(w.ToInt32()==4) collector?.OnSuspend();
                else if(w.ToInt32()==0x12) Coordinator?.Resumed(); // includes unattended wake and hibernation
            }
            switch (msg)
            {
                case 0x0312 when w.ToInt32() == GlobalHotkey.Id:
                    handled = true;
                    Dispatcher.BeginInvoke(() => TogglePanel(PanelOpening.Keyboard));    // premuta di nuovo, lo chiude
                    break;
                case WM_QUERYENDSESSION:
                    handled = true;
                    return 1;                                   // si può chiudere
                case WM_ENDSESSION when w != IntPtr.Zero:
                case WM_CLOSE:
                    handled = true;
                    Dispatcher.BeginInvoke(Quit);
                    break;
            }
            return IntPtr.Zero;
        });
        // Install the hook before registering: Windows can report its initial state immediately.
        displayNotification.Start(messages.Handle);
        if (!displayNotification.IsRegistered)
            Dispatcher.BeginInvoke(() => log?.Write("display notification registration failed: " + displayNotification.RegistrationError));
#if DEBUG
        else if (UiTest) Dispatcher.BeginInvoke(() => log?.Write("ui-test Windows display notification registered"));
#endif
    }

    void Render(DashboardState s)
    {
        if (quitting) return;
        last = s;
        var m = PanelPresenter.Present(s);
        tray?.Set(s.Battery.LevelPct, s.Phase == PowerPhase.Charging, Settings.TrayText == TrayText.Percent, m.Tooltip);
        if (panel is { IsVisible: true }) panel.Update(m, s.Target.When);
        prefs?.DashboardChanged(s);
        _ = EvaluateNoticesAsync(s);
    }

    /// <summary>Regole degli avvisi fuori dal thread della UI; una valutazione alla volta.</summary>
    async Task EvaluateNoticesAsync(DashboardState s)
    {
        if (notices is null || Store is null || !await noticeGate.WaitAsync(0)) return;
        try
        {
            var settings = Settings;
            if (settings.IsQuietAt(DateTimeOffset.UtcNow)) return;
            var notice = await Task.Run(() =>
            {
                var (reserveAt, fresh, source) = NoticeText.Estimate(s);
                var recent = Store.Samples(s.Now.AddMinutes(-35), s.Now);
                return notices.Evaluate(new NotificationInput(s.Battery, reserveAt, fresh, source, recent, s.Demo), settings.Notifications);
            });
            if (notice is not null && !quitting)
            {
                var (title, body) = NoticeText.For(notice, settings.ReservePct);
                tray?.Notify(title, body);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { log?.Write($"notices: {ex.Message}"); }
        finally { noticeGate.Release(); }
    }

    /// <summary>Controllo automatico: due minuti dopo l'avvio, poi ogni 6 ore se è passato almeno un giorno.</summary>
    void StartUpdateTimer()
    {
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            if (!Settings.AutoUpdateCheck || DateTimeOffset.UtcNow - (Settings.LastUpdateCheck ?? DateTimeOffset.MinValue) < TimeSpan.FromDays(1))
                return;
            try
            {
                var known = AvailableUpdate?.Version;
                var u = await CheckForUpdateAsync();
                if (u is not null && (known is null || u.Version.CompareTo(known) > 0))
                    tray?.Notify(L.T("UpdateNoticeTitle"), L.F("UpdateNotice", u.Version), () => ShowPreferences(PreferencesWindow.GeneralSection));
            }
            catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
            {
                log?.Write($"update check: {ex.Message}");           // offline o GitHub non raggiungibile: si riprova più tardi
            }
        };
        updateTimer.Start();
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        var current = AppVersion.Parse(Version) ?? new AppVersion(0, 0, 0, "");
        var u = await Updates.CheckAsync(current);
        AvailableUpdate = u;
        UpdateSettings(Settings with { LastUpdateCheck = DateTimeOffset.UtcNow });
        return u;
    }

    /// <summary>Avvia l'installer scaricato e verificato, in modalità con avanzamento visibile, poi esce.</summary>
    public void InstallUpdate(string installer)
    {
        Process.Start(new ProcessStartInfo(installer, "/SP- /NORESTART /RELAUNCH") { UseShellExecute = true });
        Quit();
    }

    public void TestNotification() => tray?.Notify(L.T("NoticeTestTitle"), L.T("NoticeTest"));

    void TogglePanel() => TogglePanel(PanelOpening.Click);

    void TogglePanel(PanelOpening how)
    {
        if (panel is { IsVisible: true })
        {
            // aperto passandoci sopra: il clic lo "fissa" invece di chiuderlo
            if (how == PanelOpening.Click && panel.OpenedBy == PanelOpening.Hover) { panel.Pin(); return; }
            panel.Dismiss();
            return;
        }
        // il clic sull'icona ha appena tolto il focus al pannello, che si è nascosto: non riaprirlo
        if (panel is not null && DateTime.UtcNow - panel.HiddenAt < TimeSpan.FromMilliseconds(400)) return;
        ShowPanel(how);
    }

    void ShowPanel() => ShowPanel(PanelOpening.Click);

    void ShowPanel(PanelOpening how)
    {
        if (quitting || Coordinator is null) return;
        if (panel is null)
        {
            panel = new PanelWindow(this);
            panel.IsVisibleChanged += (_, _) => tray?.SetTooltipSuppressed(panel?.IsVisible == true);
        }
        Coordinator.PanelOpened();
        var s = last ?? Coordinator.State;
        if (s is not null) panel.Show(PanelPresenter.Present(s), s.Target.When, how);
    }

    DispatcherTimer? hoverTimer;
    Native.POINT hoverStart;

    /// <summary>
    /// Modalità "apri al passaggio": se il puntatore resta sull'icona per circa mezzo secondo il pannello si apre
    /// senza prendere il focus; si richiude da solo quando il puntatore se ne va (vedi PanelWindow).
    /// </summary>
    void OnTrayHover()
    {
        if (!Settings.OpenOnHover || quitting || panel is { IsVisible: true }) return;
        if (panel is not null && DateTime.UtcNow - panel.HiddenAt < TimeSpan.FromMilliseconds(600)) return;
        if (hoverTimer is { IsEnabled: true }) return;
        Native.GetCursorPos(out hoverStart);
        hoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        hoverTimer.Tick -= HoverElapsed;
        hoverTimer.Tick += HoverElapsed;
        hoverTimer.Start();
    }

    void HoverElapsed(object? sender, EventArgs e)
    {
        hoverTimer!.Stop();
        Native.GetCursorPos(out var p);
        // ancora sopra l'icona (tolleranza = dimensione dell'icona, in pixel fisici)
        var tolerance = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        if (Math.Abs(p.X - hoverStart.X) <= tolerance && Math.Abs(p.Y - hoverStart.Y) <= tolerance)
            ShowPanel(PanelOpening.Hover);
    }

    /// <summary>Ultima lettura della batteria (per la salute nella finestra storico).</summary>
    public BatterySample? CurrentBattery => last?.Battery;

    public void ShowHistory()
    {
        if (history is null)
        {
            history = new HistoryWindow(this);
            history.Closed += (_, _) => history = null;
        }
        history.Show();
        history.Activate();
    }

    public void ShowPreferences() => ShowPreferences(null);

    /// <summary>section: indice della sezione da mostrare (null = Andamento).</summary>
    public void ShowPreferences(int? section)
    {
        if (prefs is null)
        {
            prefs = new PreferencesWindow(this);
            prefs.Closed += (_, _) => prefs = null;
        }
        prefs.ShowSection(section ?? 0);
        if (prefs.WindowState == WindowState.Minimized) prefs.WindowState = WindowState.Normal;
        prefs.Show();
        prefs.Activate();
    }

    /// <summary>Registra e salva subito la scorciatoia. Restituisce null se riuscita, altrimenti la chiave del messaggio.</summary>
    public string? SetPanelHotkey(HotkeySpec spec)
    {
        if (hotkey is not null && !hotkey.TrySet(spec)) return ShortcutError = "HotkeyConflict";
        ShortcutError = null;
        UpdateSettings(Settings with { PanelHotkey = spec.ToString() });
        return null;
    }

    public HotkeySpec CurrentHotkey => hotkey?.Current ?? (HotkeySpec.TryParse(Settings.PanelHotkey, out var s) ? s : default);

    /// <summary>Durante la registrazione dalle Preferenze la scorciatoia attuale non deve scattare.</summary>
    public void SuspendHotkey() => hotkey?.Suspend();

    public void ResumeHotkey()
    {
        if (hotkey is not null && !hotkey.Resume()) ShortcutError = "HotkeyConflict";
    }


    public void ExportDiagnostics(string destination)
    {
        using var file = new FileStream(destination, FileMode.Create, FileAccess.Write);
        using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);
        void Text(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        Text("README.txt", "Battery Forecast local diagnostics. Created explicitly by the user. No battery history or logs included.");
        Text("versions.txt", $"Battery Forecast {Version}\nArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\nRuntime: {Environment.Version}\nEngine: {EngineDescription}");
        Text("preferences.json", System.Text.Json.JsonSerializer.Serialize(Settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (var (name, path) in new[] { ("MODEL_CARD.md", Path.Combine(AppContext.BaseDirectory, "docs", "MODEL_CARD.md")),
            ("MODEL_INFO.json", Path.Combine(AppContext.BaseDirectory, "engine", "model", "MODEL_INFO.json")) })
            if (File.Exists(path)) Text(name, File.ReadAllText(path));
    }

    public void UpdateSettings(Settings next)
    {
        var prev = Settings;
        // una scorciatoia cambiata per altre vie (es. impostazioni importate) si registra qui; se Windows la rifiuta resta la precedente
        if (prev.PanelHotkey != next.PanelHotkey && hotkey is not null && HotkeySpec.TryParse(next.PanelHotkey, out var spec)
            && spec != hotkey.Current && !hotkey.TrySet(spec))
        {
            ShortcutError = "HotkeyConflict";
            next = next with { PanelHotkey = prev.PanelHotkey };
        }
        Settings = next.Normalized();
        try { Settings.Save(paths.Settings); } catch (IOException ex) { log?.Write($"settings: {ex.Message}"); }
        if (prev.Theme != Settings.Theme) { Theme.Apply(Settings.Theme); panel?.ApplyFrame(); }
        if (prev.PanelWidth != Settings.PanelWidth || prev.PanelScale != Settings.PanelScale) panel?.ApplySize();
        if (prev.Language != Settings.Language)
        {
            L.Apply(Settings.Language);
            panel?.Close();
            panel = null;
            tray?.Dispose();
            tray = new TrayIcon(TogglePanel, ShowPanel, ShowHistory, ShowPreferences, Quit, L.T, OnTrayHover);
            if (prefs is not null) { prefs.Close(); ShowPreferences(); }
        }
        if (prev.RetentionDays != Settings.RetentionDays)
            Task.Run(() => Store?.Prune(DateTimeOffset.UtcNow, Settings.RetentionDays));
        var modelChanged = prev.ReservePct != Settings.ReservePct || prev.ChargeTargetPct != Settings.ChargeTargetPct
            || prev.UseSystemContext != Settings.UseSystemContext || prev.CollectSystemContext != Settings.CollectSystemContext
            || prev.CollectProcesses!=Settings.CollectProcesses;
        if(prev.CollectProcesses!=Settings.CollectProcesses) collector?.ResetContextIntervals();
        if (prev.CollectSystemContext != Settings.CollectSystemContext || prev.CollectAdvancedSensors != Settings.CollectAdvancedSensors
            || prev.ContextIntervalSeconds != Settings.ContextIntervalSeconds || prev.CollectProcesses!=Settings.CollectProcesses)
            _ = Task.Run(() => collector?.Sample(forceContext: true));
        if(prev.UseLocalCalibration!=Settings.UseLocalCalibration || prev.CalibrationResetUtc!=Settings.CalibrationResetUtc) Coordinator?.CalibrationChanged();
        else Coordinator?.SettingsChanged(recalculate: modelChanged);

    }

    public async Task RefreshSensorsAsync()
    {
        await Task.Run(() => collector?.Sample(forceContext: true));
        if (!Settings.CollectSystemContext) return;
        await Task.Delay(1100); // allow a CPU counter delta and the optional background probes to complete
        await Task.Run(() => collector?.Sample(forceContext: true));
    }

    public void DeleteHistory()
    {
        Store?.Wipe();
        try { File.Delete(paths.LastForecast); } catch (IOException) { }
        notices?.Reset();
        Coordinator?.Reset();
    }

    void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend: collector?.OnSuspend(); break;
            case PowerModes.Resume: Coordinator?.Resumed(); break;
            case PowerModes.StatusChange: Task.Run(() => collector?.Sample()); break;
        }
    }

    void OnUserPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)) return;
        Dispatcher.BeginInvoke(() =>
        {
            Theme.Apply(Settings.Theme);
            panel?.ApplyFrame();
            if (last is not null) Render(last);
        });
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // un errore della UI non deve fermare l'icona e la raccolta dei dati
        log?.Write($"ui: {e.Exception}");
        e.Handled = true;
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.UserPreferenceChanged -= OnUserPreference;
        panel?.Close();
        prefs?.Close();
        history?.Close();
        tray?.Dispose();
        Coordinator?.Dispose();
        collector?.OnExit();
        collector?.Dispose();
        try { worker?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
        Store?.Dispose();
        hotkey?.Dispose();
        displayNotification?.Dispose();
        messages?.Dispose();
        instance?.Dispose();
        Shutdown();
    }
}

/// <summary>Log locale a rotazione (1 MB, una copia precedente). Niente dati personali oltre a quelli della batteria.</summary>
sealed class Log(string path)
{
    readonly Lock gate = new();

    public void Write(string line)
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
