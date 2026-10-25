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
        MaxHeight = SystemParameters.WorkArea.Height - 40;     // il resto scorre
        var s = app.Settings;
        CollectContext.IsChecked = s.CollectSystemContext;
        UseContext.IsChecked = s.UseSystemContext;
        UseContext.IsEnabled = s.CollectSystemContext;

        Reserve.Text = s.ReservePct.ToString("0.##", CultureInfo.InvariantCulture);
        Target.Text = s.TargetTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        ExtraTimes.Text = string.Join(", ", s.AdditionalTargets.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        ChargeTarget.Text = s.ChargeTargetPct.ToString(CultureInfo.InvariantCulture);
        Hotkey.Text = s.PanelHotkey;
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
        foreach (var c in new[] { NotifyEnabled, NotifyDrain, ShowChart, KeepOpenDefault, QuietEnabled, CollectContext, UseContext }) c.Click += (_, _) => Save();
        TrayPercent.Click += (_, _) => Save();
        Login.Click += (_, _) =>
        {
            try { LoginItem.Set(Login.IsChecked == true); }
            catch (UnauthorizedAccessException) { }
            Login.IsChecked = LoginItem.IsEnabled;
            Save();
        };
        foreach (var box in new[] { Reserve, Target, ExtraTimes, ChargeTarget, Hotkey, QuietStart, QuietEnd, AlertMinutes, NotifyLow, NotifyCharged })
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyCustom(); e.Handled = true; } };
        if (app.ShortcutError is { } error) CustomStatus.Text = L.T(error);
        loading = false;
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
            CollectSystemContext = CollectContext.IsChecked == true,
            UseSystemContext = UseContext.IsChecked == true,
            ShowChart = ShowChart.IsChecked == true,
            KeepPanelOpen = KeepOpenDefault.IsChecked == true,
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
        UseContext.IsEnabled = CollectContext.IsChecked == true;
    }

    void Custom_Click(object sender, RoutedEventArgs e) => ApplyCustom();

    void ApplyCustom()
    {
        static bool Time(string text, out TimeOnly value) => TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        var extra = new List<TimeOnly>();
        bool validExtra = true;
        foreach (var part in ExtraTimes.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        { if (Time(part, out var t)) extra.Add(t); else validExtra = false; }
        var minutes = new List<int>();
        bool validMinutes = true;
        foreach (var part in AlertMinutes.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        { if (int.TryParse(part, out var m) && m is >= 5 and <= 600) minutes.Add(m); else validMinutes = false; }
        if (!double.TryParse(Reserve.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var reserve) || reserve is < 1 or > 30 || !double.IsFinite(reserve) ||
            !Time(Target.Text, out var target) || !Time(QuietStart.Text, out var quietStart) || !Time(QuietEnd.Text, out var quietEnd) ||
            !validExtra || extra.Distinct().Count() > 4 || !validMinutes ||
            !int.TryParse(ChargeTarget.Text, out var chargeTarget) || chargeTarget is < 50 or > 100 ||
            !int.TryParse(NotifyLow.Text, out var low) || low is < 0 or > 50 ||
            !int.TryParse(NotifyCharged.Text, out var charged) || !(charged == 0 || charged is >= 50 and <= 100) ||
            !HotkeySpec.TryParse(Hotkey.Text, out _))
        { CustomStatus.Text = NotifyCustomStatus.Text = L.T("CustomInvalid"); return; }
        if (!app.TrySetPanelHotkey(Hotkey.Text))
        { CustomStatus.Text = NotifyCustomStatus.Text = L.T("HotkeyConflict"); return; }
        app.UpdateSettings(app.Settings with
        {
            ReservePct = reserve, TargetTime = target, AdditionalTargets = [.. extra], ChargeTargetPct = chargeTarget,
            PanelHotkey = Hotkey.Text.Trim(), QuietStart = quietStart, QuietEnd = quietEnd,
            Notifications = app.Settings.Notifications with { MinutesLeft = [.. minutes], LowPct = low, ChargedPct = charged },
        });
        CustomStatus.Text = NotifyCustomStatus.Text = L.T("CustomSaved");
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
