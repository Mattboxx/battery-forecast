using System.IO;
using System.Windows;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

/// <summary>Storico delle scariche osservate e salute della batteria. I dati si leggono fuori dal thread della UI.</summary>
public partial class HistoryWindow : Window
{
    readonly App app;

    public HistoryWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is null) await LoadAsync(); };
    }

    async Task LoadAsync()
    {
        if (app.Store is not { } store) return;
        var current = app.CurrentBattery;
        var model = await Task.Run(() =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-app.Settings.RetentionDays);
            return HistoryPresenter.Present(store.Samples(since), store.Discontinuities(since), current);
        });
        DataContext = model;
        Empty.Visibility = model.Segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"till-dusk-history-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV|*.csv",
        };
        if (dlg.ShowDialog(this) != true || app.Store is null) return;
        using var w = new StreamWriter(dlg.FileName);
        app.Store.ExportCsv(w);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
