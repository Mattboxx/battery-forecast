using System.Diagnostics;
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

        Fill(Reserve, new double[] { 3, 5, 7, 10, 15, 20 }.Select(v => ($"{v:0}%", (object)v)), s.ReservePct);
        Fill(Target, Enumerable.Range(34, 14).Select(i => new TimeOnly(i / 2, i % 2 * 30))
            .Select(t => (t.ToString("t", System.Globalization.CultureInfo.CurrentCulture), (object)t)), s.TargetTime);
        Fill(RefreshBox, [(L.T("RefreshOnOpen"), RefreshPolicy.OnOpen), (L.T("RefreshBalanced"), RefreshPolicy.Balanced),
            (L.T("RefreshFrequent"), RefreshPolicy.Frequent)], s.Refresh);
        Fill(ThemeBox, [(L.T("ThemeSystem"), ThemeChoice.System), (L.T("ThemeLight"), ThemeChoice.Light), (L.T("ThemeMist"), ThemeChoice.Mist),
            (L.T("ThemeDark"), ThemeChoice.Dark), (L.T("ThemeMidnight"), ThemeChoice.Midnight)], s.Theme);
        Fill(LanguageBox, [(L.T("LangSystem"), LanguageChoice.System), ("Italiano", LanguageChoice.Italian), ("English", LanguageChoice.English)], s.Language);
        Fill(Retention, new[] { 30, 90, 180, 365 }.Select(d => (L.F("Days", d), (object)d)), s.RetentionDays);
        var n = s.Notifications;
        NotifyEnabled.IsChecked = n.Enabled;
        NotifyTime.IsChecked = n.MinutesLeft.Length > 0;
        Fill(NotifyLow, new[] { 0, 5, 10, 15, 20 }.Select(v => (v == 0 ? L.T("Off") : $"{v}%", (object)v)), n.LowPct);
        Fill(NotifyCharged, new[] { 0, 80, 90, 100 }.Select(v => (v == 0 ? L.T("Off") : $"{v}%", (object)v)), n.ChargedPct);
        NotifyDrain.IsChecked = n.UnusualDrain;
        NotifyOptions.IsEnabled = n.Enabled;
        TrayPercent.IsChecked = s.TrayText == TrayText.Percent;
        Login.IsChecked = LoginItem.IsEnabled;
        About.Text = L.F("AboutText", app.Version, app.EngineDescription);
        AutoUpdate.IsChecked = s.AutoUpdateCheck;
        AutoUpdate.Click += (_, _) => Save();
        ShowUpdate(app.AvailableUpdate);

        foreach (var c in new[] { Reserve, Target, RefreshBox, ThemeBox, LanguageBox, Retention, NotifyLow, NotifyCharged }) c.SelectionChanged += (_, _) => Save();
        foreach (var c in new[] { NotifyEnabled, NotifyTime, NotifyDrain }) c.Click += (_, _) => Save();
        TrayPercent.Click += (_, _) => Save();
        Login.Click += (_, _) =>
        {
            try { LoginItem.Set(Login.IsChecked == true); }
            catch (UnauthorizedAccessException) { }
            Login.IsChecked = LoginItem.IsEnabled;
            Save();
        };
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
            ReservePct = Value<double>(Reserve),
            TargetTime = Value<TimeOnly>(Target),
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
                MinutesLeft = NotifyTime.IsChecked == true ? [60, 30, 15] : [],
                LowPct = Value<int>(NotifyLow),
                ChargedPct = Value<int>(NotifyCharged),
                UnusualDrain = NotifyDrain.IsChecked == true,
            },
        });
        NotifyOptions.IsEnabled = NotifyEnabled.IsChecked == true;
    }

    void TestNotify_Click(object sender, RoutedEventArgs e) => app.TestNotification();

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"till-dusk-history-{DateTime.Now:yyyy-MM-dd}.csv",
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
