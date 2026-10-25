using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatteryForecast.Core;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

/// <summary>
/// Immagini del pannello e delle preferenze in chiaro e scuro, dalla modalità demo: per controllare la
/// grafica senza pilotare lo schermo e per il README (dichiarate come demo).
/// </summary>
static class Snapshots
{
    public static void Save(App app, DashboardState state, string dir)
    {
        Directory.CreateDirectory(dir);
        var model = PanelPresenter.Present(state);
        foreach (var theme in new[] { ThemeChoice.Dark, ThemeChoice.Light, ThemeChoice.Midnight, ThemeChoice.Mist })
        {
            Theme.Apply(theme);
            var name = theme.ToString().ToLowerInvariant();
            var panel = new PanelWindow(app) { Left = -20000, Top = -20000, ShowActivated = false };
            panel.Update(model, state.Target.When);
            Render(panel, Path.Combine(dir, $"panel-{name}.png"));
            var prefs = new PreferencesWindow(app) { Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Render(prefs, Path.Combine(dir, $"preferences-{name}.png"));
            var history = new HistoryWindow(app) { Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            history.DataContext = HistoryPresenter.Present(app.Store!.Samples(since), app.Store.Discontinuities(since), state.Battery);
            Render(history, Path.Combine(dir, $"history-{name}.png"));
        }
        foreach (var (level, charging, percent, dark) in new[] { (63.0, false, false, true), (12.0, false, false, true), (80.0, true, false, true), (63.0, false, true, true), (63.0, false, false, false) })
        {
            using var bmp = TrayIcon.Draw(level, charging, percent, dark, 32);
            bmp.Save(Path.Combine(dir, $"tray-{level:0}{(charging ? "-charging" : "")}{(percent ? "-percent" : "")}{(dark ? "-dark" : "-light")}.png"));
        }
        Theme.Apply(app.Settings.Theme);
    }

    static void Render(Window w, string path)
    {
        ((Window)w).Show();
        w.UpdateLayout();
        // la radice del template della finestra contiene sfondo, bordo e margini del contenuto
        var root = (Visual)VisualTreeHelper.GetChild(w, 0);
        const double scale = 2;
        // l'area client: le finestre con cornice di sistema hanno anche la barra del titolo, che non è nel visuale
        var size = root is FrameworkElement fe ? new Size(fe.ActualWidth, fe.ActualHeight) : new Size(w.ActualWidth, w.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(size) }, null, new Rect(size));
        var bmp = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
        w.Close();
    }
}
