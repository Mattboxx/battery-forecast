using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BatteryForecast.Core;
using BatteryForecast.Core.Presentation;
using BatteryForecast.Core.Updates;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.App;

/// <summary>Preferenze: ogni modifica si salva subito (nessun pulsante "Applica").</summary>
public partial class PreferencesWindow : Window
{
    readonly App app;
    bool loading = true;

    public PreferencesWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        PreviewMouseDown += (_, e) =>
        {
            if (!new Rect(TrendChart.RenderSize).Contains(e.GetPosition(TrendChart))) TrendChart.ClearSelection();
            foreach (var chart in new[] { PowerHistory, CpuHistory, MemoryHistory, DisplayHistory })
                if (!new Rect(chart.RenderSize).Contains(e.GetPosition(chart))) chart.ClearSelection();
        };
        Deactivated += (_, _) => { TrendChart.ClearSelection(); PowerHistory.ClearSelection(); CpuHistory.ClearSelection(); MemoryHistory.ClearSelection(); DisplayHistory.ClearSelection(); };
        MaxHeight = SystemParameters.WorkArea.Height - 40;     // il resto scorre
        pages = [PageTrend, PageForecast, PagePanel, PageNotify, PageLook, PageData, PageGeneral];
        Sections.SelectionChanged += (_, _) => ShowSection(Sections.SelectedIndex);
        Sections.SelectedIndex = 0;
        var s = app.Settings;
        TrendObservedToggle.IsChecked = s.TrendShowObserved;
        TrendForecastToggle.IsChecked = s.TrendShowForecast;
        TrendObservedToggle.Click += TrendVisibility_Click;
        TrendForecastToggle.Click += TrendVisibility_Click;
        EveningEnabled.IsChecked = s.EveningTargetEnabled;
        Target.IsEnabled = ExtraTimes.IsEnabled = s.EveningTargetEnabled;
        AdvancedSensors.IsChecked = s.CollectAdvancedSensors;
        AdvancedSensors.IsEnabled = s.CollectSystemContext;
        ContextInterval.Text = s.ContextIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        ContextHours.Text = s.ContextHistoryHours.ToString(CultureInfo.InvariantCulture);
        CollectContext.IsChecked = s.CollectSystemContext;
        UseContext.IsChecked = s.UseSystemContext;
        UseContext.IsEnabled = s.CollectSystemContext;

        Reserve.Text = s.ReservePct.ToString("0.##", CultureInfo.InvariantCulture);
        Target.Text = s.TargetTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        ExtraTimes.Text = string.Join(", ", s.AdditionalTargets.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        ChargeTarget.Text = s.ChargeTargetPct.ToString(CultureInfo.InvariantCulture);
        ShowHotkey(app.ShortcutError is null ? null : L.F("HotkeyConflict", s.PanelHotkey));
        OpenOnHover.IsChecked = s.OpenOnHover;
        ScaleSlider.Value = s.PanelScale;
        WidthSlider.Value = s.PanelWidth;
        ShowSize();
        sizeTimer.Tick += (_, _) => { sizeTimer.Stop(); Save(); };
        foreach (var slider in new[] { ScaleSlider, WidthSlider })
            slider.ValueChanged += (_, _) => { ShowSize(); if (!loading) { sizeTimer.Stop(); sizeTimer.Start(); } };
        HotkeyBox.PreviewKeyDown += HotkeyBox_KeyDown;
        HotkeyBox.LostKeyboardFocus += (_, _) => StopRecording(apply: null);
        HotkeyBox.MouseLeftButtonDown += (_, _) => StartRecording();
        Closed += (_, _) => StopRecording(apply: null);
        QuietStart.Text = s.QuietStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        QuietEnd.Text = s.QuietEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        QuietEnabled.IsChecked = s.QuietHoursEnabled;
        ShowChart.IsChecked = s.ShowChart;
        KeepOpenDefault.IsChecked = s.KeepPanelOpen;
        Fill(RefreshBox, [(L.T("RefreshOnOpen"), RefreshPolicy.OnOpen), (L.T("RefreshBalanced"), RefreshPolicy.Balanced),
            (L.T("RefreshFrequent"), RefreshPolicy.Frequent)], s.Refresh);
        Fill(ThemeBox, [(L.T("ThemeSystem"), ThemeChoice.System), (L.T("ThemeLight"), ThemeChoice.Light), (L.T("ThemeMist"), ThemeChoice.Mist),
            (L.T("ThemeDark"), ThemeChoice.Dark), (L.T("ThemeMidnight"), ThemeChoice.Midnight)], s.Theme);
        Fill(LanguageBox, [(L.T("LangSystem"), LanguageChoice.System), ("Italiano", LanguageChoice.Italian), ("English", LanguageChoice.English)], s.Language);
        Fill(Retention, new[] { 30, 90, 180, 365 }.Select(d => (L.F("Days", d), (object)d)), s.RetentionDays);
        var n = s.Notifications;
        NotifyEnabled.IsChecked = n.Enabled;
        AlertMinutes.Text = string.Join(", ", n.MinutesLeft);
        NotifyLow.Text = n.LowPct.ToString(CultureInfo.InvariantCulture);
        NotifyCharged.Text = n.ChargedPct.ToString(CultureInfo.InvariantCulture);
        NotifyDrain.IsChecked = n.UnusualDrain;
        NotifyOptions.IsEnabled = n.Enabled;
        TrayPercent.IsChecked = s.TrayText == TrayText.Percent;
        Login.IsChecked = LoginItem.IsEnabled;
        About.Text = L.F("AboutText", app.Version, app.EngineDescription);
        AutoUpdate.IsChecked = s.AutoUpdateCheck;
        AutoUpdate.Click += (_, _) => Save();
        ShowUpdate(app.AvailableUpdate);

        foreach (var c in new[] { RefreshBox, ThemeBox, LanguageBox, Retention }) c.SelectionChanged += (_, _) => Save();
        foreach (var c in new[] { NotifyEnabled, NotifyDrain, ShowChart, KeepOpenDefault, OpenOnHover, QuietEnabled, CollectContext, UseContext, EveningEnabled, AdvancedSensors }) c.Click += (_, _) => Save();
        TrayPercent.Click += (_, _) => Save();
        Login.Click += (_, _) =>
        {
            try { LoginItem.Set(Login.IsChecked == true); }
            catch (UnauthorizedAccessException) { }
            Login.IsChecked = LoginItem.IsEnabled;
            Save();
        };
        foreach (var box in new[] { Reserve, Target, ExtraTimes, ChargeTarget, QuietStart, QuietEnd, AlertMinutes, NotifyLow, NotifyCharged })
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyCustom(); e.Handled = true; } };
        loading = false;
        TrendForecasts.SelectionChanged += (_, _) => { if (!loadingTrend) ShowTrend(); };
        Loaded += (_, _) => { if (Sections.SelectedIndex == 5) LoadContextHistory(); if (Sections.SelectedIndex == 0) LoadTrend(); };
        ContextInterval.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveContext_Click(this, e); e.Handled = true; } };
        ContextHours.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveContext_Click(this, e); e.Handled = true; } };
    }

    // MARK: sezioni

    readonly ScrollViewer[] pages;


    public int SectionCount => pages.Length;
    public const int GeneralSection = 6;     // aggiornamenti e informazioni

    public void ShowSection(int index)
    {
        if (index < 0 || index >= pages.Length) return;
        if (Sections.SelectedIndex != index) { Sections.SelectedIndex = index; return; }
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        pages[index].ScrollToTop();
        if (index == 0 && !loading) LoadTrend();

        if (index == 5 && !loading) LoadContextHistory();
    }

    static void Fill(ComboBox box, IEnumerable<(string text, object value)> items, object current)
    {
        foreach (var (text, value) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            box.Items.Add(item);
            if (Equals(value, current)) box.SelectedItem = item;
        }
        box.SelectedIndex = box.SelectedIndex < 0 ? 0 : box.SelectedIndex;
    }

    static T Value<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem).Tag;

    void Save()
    {
        if (loading) return;
        app.UpdateSettings(app.Settings with
        {
            EveningTargetEnabled = EveningEnabled.IsChecked == true,
            CollectAdvancedSensors = AdvancedSensors.IsChecked == true,
            CollectSystemContext = CollectContext.IsChecked == true,
            UseSystemContext = UseContext.IsChecked == true,
            ShowChart = ShowChart.IsChecked == true,
            KeepPanelOpen = KeepOpenDefault.IsChecked == true,
            OpenOnHover = OpenOnHover.IsChecked == true,
            PanelScale = ScaleSlider.Value,
            PanelWidth = WidthSlider.Value,
            QuietHoursEnabled = QuietEnabled.IsChecked == true,
            Refresh = Value<RefreshPolicy>(RefreshBox),
            Theme = Value<ThemeChoice>(ThemeBox),
            Language = Value<LanguageChoice>(LanguageBox),
            RetentionDays = Value<int>(Retention),
            TrayText = TrayPercent.IsChecked == true ? TrayText.Percent : TrayText.None,
            LaunchAtLogin = LoginItem.IsEnabled,
            AutoUpdateCheck = AutoUpdate.IsChecked == true,
            Notifications = app.Settings.Notifications with
            {
                Enabled = NotifyEnabled.IsChecked == true,
                UnusualDrain = NotifyDrain.IsChecked == true,
            },
        });
        NotifyOptions.IsEnabled = NotifyEnabled.IsChecked == true;
        UseContext.IsEnabled = AdvancedSensors.IsEnabled = CollectContext.IsChecked == true;
        Target.IsEnabled = ExtraTimes.IsEnabled = EveningEnabled.IsChecked == true;
    }

    void Custom_Click(object sender, RoutedEventArgs e) => ApplyCustom();

    // MARK: pannello

    readonly System.Windows.Threading.DispatcherTimer sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    void ShowSize()
    {
        ScaleValue.Text = $"{Math.Round(ScaleSlider.Value * 100):0}%";
        WidthValue.Text = $"{WidthSlider.Value:0}";
    }

    void SizeReset_Click(object sender, RoutedEventArgs e)
    {
        ScaleSlider.Value = 1.0;
        WidthSlider.Value = Settings.DefaultPanelWidth;
    }

    // MARK: scorciatoia

    bool recording;

    void ShowHotkey(string? status = null)
    {
        var current = app.CurrentHotkey;
        HotkeyText.Text = recording ? L.T("HotkeyRecording") : current.IsEmpty ? L.T("HotkeyNone") : current.ToString();
        HotkeyStatus.Text = status ?? (recording ? L.T("HotkeyRecordingHelp")
            : current.IsEmpty ? L.T("HotkeyOff") : L.F("HotkeyActive", current));
        HotkeyChange.IsEnabled = !recording;
    }

    void HotkeyChange_Click(object sender, RoutedEventArgs e) => StartRecording();

    void StartRecording()
    {
        if (recording) return;
        recording = true;
        app.SuspendHotkey();            // altrimenti Windows consumerebbe la combinazione attuale prima di noi
        ShowHotkey();
        HotkeyBox.Focus();
        Keyboard.Focus(HotkeyBox);
    }

    /// <summary>apply = combinazione da salvare; null = annulla e ripristina la precedente.</summary>
    void StopRecording(HotkeySpec? apply)
    {
        if (!recording) return;
        recording = false;
        app.ResumeHotkey();
        string? status = null;
        if (apply is { } spec && app.SetPanelHotkey(spec) is not null) status = L.F("HotkeyConflict", spec);
        ShowHotkey(status);
    }

    void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!recording) { if (e.Key is Key.Enter or Key.Space) { StartRecording(); e.Handled = true; } return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(apply: null); return; }
        // si aspetta il tasto "vero": i soli modificatori non bastano
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
        var m = Keyboard.Modifiers;
        uint mods = (m.HasFlag(ModifierKeys.Control) ? HotkeySpec.Ctrl : 0) | (m.HasFlag(ModifierKeys.Alt) ? HotkeySpec.Alt : 0)
                  | (m.HasFlag(ModifierKeys.Shift) ? HotkeySpec.Shift : 0) | (m.HasFlag(ModifierKeys.Windows) ? HotkeySpec.Win : 0);
        if ((mods & (HotkeySpec.Ctrl | HotkeySpec.Alt | HotkeySpec.Win)) == 0) { HotkeyStatus.Text = L.T("HotkeyNeedsModifier"); return; }
        if (!HotkeySpec.TryCreate(mods, (uint)KeyInterop.VirtualKeyFromKey(key), out var spec)) { HotkeyStatus.Text = L.T("HotkeyUnsupportedKey"); return; }
        StopRecording(spec);
    }

    void HotkeyDefault_Click(object sender, RoutedEventArgs e)
    {
        StopRecording(apply: null);
        HotkeySpec.TryParse("Ctrl+Alt+B", out var spec);
        ShowHotkey(app.SetPanelHotkey(spec) is null ? null : L.F("HotkeyConflict", spec));
    }

    void HotkeyDisable_Click(object sender, RoutedEventArgs e)
    {
        StopRecording(apply: null);
        app.SetPanelHotkey(default);
        ShowHotkey();
    }

    void ApplyCustom()
    {
        static bool Time(string text, out TimeOnly value) => TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        var target = app.Settings.TargetTime;
        var validTarget = EveningEnabled.IsChecked != true || Time(Target.Text, out target);
        var extra = new List<TimeOnly>();
        bool validExtra = true;
        foreach (var part in (EveningEnabled.IsChecked == true ? ExtraTimes.Text : string.Join(",", app.Settings.AdditionalTargets.Select(t => t.ToString("HH:mm")))).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        { if (Time(part, out var t)) extra.Add(t); else validExtra = false; }
        var minutes = new List<int>();
        bool validMinutes = true;
        foreach (var part in AlertMinutes.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        { if (int.TryParse(part, out var m) && m is >= 5 and <= 600) minutes.Add(m); else validMinutes = false; }
        if (!double.TryParse(Reserve.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var reserve) || reserve is < 1 or > 30 || !double.IsFinite(reserve) ||
            !validTarget || !Time(QuietStart.Text, out var quietStart) || !Time(QuietEnd.Text, out var quietEnd) ||
            !validExtra || extra.Distinct().Count() > 4 || !validMinutes ||
            !int.TryParse(ChargeTarget.Text, out var chargeTarget) || chargeTarget is < 50 or > 100 ||
            !int.TryParse(NotifyLow.Text, out var low) || low is < 0 or > 50 ||
            !int.TryParse(NotifyCharged.Text, out var charged) || !(charged == 0 || charged is >= 50 and <= 100))
        { CustomStatus.Text = NotifyCustomStatus.Text = L.T("CustomInvalid"); return; }
        app.UpdateSettings(app.Settings with
        {
            ReservePct = reserve, TargetTime = target, AdditionalTargets = [.. extra], ChargeTargetPct = chargeTarget,
            QuietStart = quietStart, QuietEnd = quietEnd,
            Notifications = app.Settings.Notifications with { MinutesLeft = [.. minutes], LowPct = low, ChargedPct = charged },
        });
        CustomStatus.Text = NotifyCustomStatus.Text = L.T("CustomSaved");
    }

    sealed record TrendChoice(ForecastSnapshot Snapshot, string Label);
    IReadOnlyList<BatterySample> trendSamples = [];
    IReadOnlyList<DateTimeOffset> trendBreaks = [];
    DateTimeOffset trendNow;
    bool loadingTrend, trendWasUpdating;
    DateTimeOffset? trendLoadedBattery;

    public void DashboardChanged(DashboardState state)
    {
        if (!IsVisible || Sections.SelectedIndex != 0) return;
        if (state.Updating) { trendWasUpdating = true; return; }
        if (trendWasUpdating || trendLoadedBattery != state.Battery.TimestampUtc)
        {
            trendWasUpdating = false;
            LoadTrend();
        }
    }

    void RefreshTrend_Click(object sender, RoutedEventArgs e) => LoadTrend();

    void TrendVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        app.UpdateSettings(app.Settings with
        {
            TrendShowObserved = TrendObservedToggle.IsChecked == true,
            TrendShowForecast = TrendForecastToggle.IsChecked == true,
        });
        ShowTrend();
    }

    void LoadTrend()
    {
        if (app.Store is not { } store) return;
        try
        {
            trendNow = DateTimeOffset.UtcNow;
            trendLoadedBattery = app.CurrentBattery?.TimestampUtc;
            var selectedId = (TrendForecasts.SelectedItem as TrendChoice)?.Snapshot.Id;
            trendSamples = store.Samples(trendNow.AddHours(-36), trendNow);
            trendBreaks = store.Discontinuities(trendNow.AddHours(-36));
            var snapshots = store.ForecastSnapshots(trendNow.AddHours(-24), trendNow);
            var choices = snapshots.Select(p => new TrendChoice(p,
                L.F("TrendChoice", p.IssuedAt.ToLocalTime().ToString("dd/MM HH:mm", L.Culture),
                    L.T(p.Response.Scenario == "charge" ? "TrendCharge" : "TrendDischarge")))).ToArray();
            loadingTrend = true;
            TrendForecasts.ItemsSource = choices;
            TrendForecasts.SelectedItem = choices.FirstOrDefault(p => p.Snapshot.Id == selectedId)
                ?? choices.FirstOrDefault(p => TrendComparison.Latest(p.Snapshot, trendSamples, trendBreaks) is not null)
                ?? choices.FirstOrDefault();
            loadingTrend = false;
            ShowTrend();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { loadingTrend = false; TrendStatus.Text = L.T("ContextReadFailed"); }
    }

    void ShowTrend()
    {
        var saved = (TrendForecasts.SelectedItem as TrendChoice)?.Snapshot;
        TrendChart.SetData(saved, trendSamples, trendBreaks, trendNow);
        TrendSource.Text = saved is null ? L.T("TrendNoSaved") : L.F("TrendSource",
            saved.View.Generated.ToLocalTime().ToString("dd/MM HH:mm", L.Culture), saved.Response.ModelId ?? "Timestral",
            L.T(saved.Response.Scenario == "charge" ? "TrendChargeScenario" : "TrendDischargeScenario"));
        TrendStatus.Text = TrendComparison.Latest(saved, trendSamples, trendBreaks) is { } latest
            ? TrendComparison.Describe(latest) : saved is null ? L.T("TrendNoSaved") : L.T("TrendWaiting");
        if (app.Coordinator?.State?.Demo == true) TrendSource.Text = L.T("DemoContextWarning") + Environment.NewLine + TrendSource.Text;
        TrendChart.SetDisplay(app.Settings.TrendShowObserved, app.Settings.TrendShowForecast);
        if (!app.Settings.TrendShowObserved && !app.Settings.TrendShowForecast) TrendStatus.Text = L.T("TrendNoCurves");
    }

    void SaveContext_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ContextInterval.Text, out var seconds) || seconds is < 30 or > 120 ||
            !int.TryParse(ContextHours.Text, out var hours) || hours is < 1 or > 168)
        { ContextSettingsStatus.Text = L.T("ContextSettingsInvalid"); return; }
        app.UpdateSettings(app.Settings with { ContextIntervalSeconds = seconds, ContextHistoryHours = hours });
        ContextSettingsStatus.Text = L.T("CustomSaved");
        LoadContextHistory();
    }

    async void RefreshSensors_Click(object sender, RoutedEventArgs e)
    {
        RefreshSensors.IsEnabled = false;
        try { await app.RefreshSensorsAsync(); if (IsVisible) LoadContextHistory(); }
        finally { RefreshSensors.IsEnabled = true; }
    }

    void LoadContextHistory()
    {
        if (app.Store is not { } store) return;
        try
        {
            var end = DateTimeOffset.UtcNow;
            var start = end.AddHours(-app.Settings.ContextHistoryHours);
            var series = store.ModelCovariates(start, end, includeLegacyCpu: true);
            var state = app.Coordinator?.State;
            var forecast = state?.Phase switch
            {
                Core.Forecasting.PowerPhase.Charging => state.ChargeForecast,
                Core.Forecasting.PowerPhase.PluggedIn => state.HeldForecast,
                _ => state?.Forecast,
            };
            ContextStatus.Text = ContextPresenter.Present(app.Settings, app.CurrentSystemContext, store.ContextSummary(start), forecast?.ContextFeatures ?? []);
            if (state?.Demo == true) ContextStatus.Text = L.T("DemoContextWarning") + Environment.NewLine + ContextStatus.Text;
            var charge = series["charge_w"].ToDictionary(p => p.Ts, p => p.Value);
            var watts = series["discharge_w"].Select(p => new Core.Storage.ModelCovariate(p.Ts,
                p.Value ?? (charge.GetValueOrDefault(p.Ts) is { } w ? -w : null))).ToArray();
            PowerHistory.SetData(watts, start, end, "W");
            CpuHistory.SetData(series["cpu_pct"], start, end, "%", true);
            MemoryHistory.SetData(series["memory_pct"], start, end, "%", true);
            DisplayHistory.SetData(series["display_state"], start, end, "", displayState: true);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { ContextStatus.Text = L.T("ContextReadFailed"); }
    }

    void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"battery-forecast-diagnostics-{DateTime.Now:yyyy-MM-dd}.zip", Filter = "ZIP|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { app.ExportDiagnostics(dialog.FileName); Note(L.T("DiagnosticsSaved")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note(ex.Message); }
    }

    void TestNotify_Click(object sender, RoutedEventArgs e) => app.TestNotification();

    void ExportContext_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"battery-forecast-context-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV|*.csv" };
        if(dialog.ShowDialog(this)!=true)return;
        try { using var writer=new StreamWriter(dialog.FileName); Note(L.F("Exported",app.Store!.ExportContextCsv(writer))); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Note(ex.Message);}
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"battery-forecast-history-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV|*.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        using var w = new StreamWriter(dlg.FileName);
        Note(L.F("Exported", app.Store!.ExportCsv(w)));
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.T("DeleteConfirm"), "Battery Forecast", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        app.DeleteHistory();
        Note(L.T("Deleted"));
    }

    void Note(string text)
    {
        DataNote.Text = text;
        DataNote.Visibility = Visibility.Visible;
    }

    void Status(string text)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Visibility = Visibility.Visible;
    }

    void ShowUpdate(UpdateInfo? u)
    {
        InstallButton.Visibility = NotesButton.Visibility = u is null ? Visibility.Collapsed : Visibility.Visible;
        if (u is not null) Status(L.F("UpdateAvailable", u.Version));
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        Status(L.T("UpdateChecking"));
        try
        {
            var u = await app.CheckForUpdateAsync();
            if (u is null) Status(L.F("UpdateNone", app.Version));
            ShowUpdate(u);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
        {
            Status(L.F("UpdateFailed", ex.Message));
        }
        finally { CheckButton.IsEnabled = true; }
    }

    async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (app.AvailableUpdate is not { } u) return;
        InstallButton.IsEnabled = CheckButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => Status(L.F("UpdateDownloading", Math.Round(p * 100))));
            var path = await app.Updates.DownloadAsync(u, progress);
            Status(L.T("UpdateStarting"));
            app.InstallUpdate(path);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            Status(L.F("UpdateFailed", ex.Message));
            InstallButton.IsEnabled = CheckButton.IsEnabled = true;
        }
    }

    void Notes_Click(object sender, RoutedEventArgs e) => Open(app.AvailableUpdate?.PageUrl);

    void Support_Click(object sender, RoutedEventArgs e) => Open(App.SupportUrl);

    static void Open(string? url)
    {
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    void Licenses_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "licenses");
        if (!Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
