using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

/// <summary>Come è stato aperto il pannello: decide posizione, focus e chiusura.</summary>
public enum PanelOpening { Click, Hover, Keyboard }

/// <summary>
/// Il pannello che si apre dall'icona. Si chiude con Esc o perdendo il focus, come i riquadri di sistema;
/// chiuderlo non chiude l'app.
/// </summary>
public partial class PanelWindow : Window
{
    readonly App app;
    Native.POINT anchor;
    bool anchored, placementQueued, dismissing;
    int animationVersion;
    const int Gap = 14;          // distanza dal bordo dell'area di lavoro, in pixel logici

    public PanelWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        if (app.UiTest) ShowInTaskbar = true;
        ApplySize();
        hoverWatch.Tick += (_, _) => WatchHover();
        KeepOpen.IsChecked = app.Settings.KeepPanelOpen;
        KeepOpen.Click += (_, _) => app.UpdateSettings(app.Settings with { KeepPanelOpen = KeepOpen.IsChecked == true });
        SourceInitialized += (_, _) =>
        {
            ApplyFrame();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        };
        SizeChanged += (_, _) => QueuePlacement();
        PreviewMouseDown += (_, e) =>
        {
            var point = e.GetPosition(Chart);
            if (!new Rect(Chart.RenderSize).Contains(point)) Chart.ClearSelection();
        };
        Deactivated += (_, _) => { Chart.ClearSelection(); if (KeepOpen.IsChecked != true) Dismiss(); };
        // un clic dentro un pannello aperto al passaggio lo rende un pannello normale
        Activated += (_, _) => { if (OpenedBy == PanelOpening.Hover) Pin(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
        };
    }

    /// <summary>Quando il pannello si è nascosto perdendo il focus: un clic sull'icona subito dopo non deve riaprirlo.</summary>
    public DateTime HiddenAt { get; private set; }

    public PanelOpening OpenedBy { get; private set; }

    public void Show(PanelModel model, DateTimeOffset target, PanelOpening how = PanelOpening.Click)
    {
        Update(model, target);
        ++animationVersion;
        dismissing = false;
        IsHitTestVisible = true;
        BeginAnimation(OpacityProperty, null);
        if (IsVisible) { Opacity = 1; if (how != PanelOpening.Hover) { Pin(); Activate(); } return; }
        OpenedBy = how;
        // da tastiera il puntatore può essere ovunque: il pannello va vicino all'area di notifica
        if (how == PanelOpening.Keyboard && TrayCorner() is { } corner) anchor = corner;
        else Native.GetCursorPos(out anchor);
        anchored = true;
        // aperto al passaggio non prende il focus: chi sta scrivendo altrove non viene interrotto
        ShowActivated = how != PanelOpening.Hover;
        Opacity = 0;
        Show();
        // Enter the destination monitor before measuring, so WPF processes its DPI change.
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, IntPtr.Zero, anchor.X, anchor.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        PlaceAtAnchor();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!IsVisible || dismissing) return;
            PlaceAtAnchor();
            AnimateOpacity(1, null);
        });
        if (how == PanelOpening.Hover)
        {
            hoverLeftAt = null;
            hoverWatch.Start();
            return;
        }
        Activate();
        Native.SetForegroundWindow(hwnd);
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>Da pannello "al passaggio" a pannello normale: resta aperto finché non si clicca fuori o si preme Esc.</summary>
    public void Pin()
    {
        if (OpenedBy != PanelOpening.Hover) return;
        OpenedBy = PanelOpening.Click;
        hoverWatch.Stop();
        if (!IsActive) { Activate(); Native.SetForegroundWindow(new WindowInteropHelper(this).Handle); }
    }

    readonly DispatcherTimer hoverWatch = new() { Interval = TimeSpan.FromMilliseconds(120) };
    DateTime? hoverLeftAt;

    /// <summary>
    /// Pannello aperto al passaggio: si richiude quando il puntatore non è né sul pannello né vicino all'icona
    /// per più di 0,6 s. "Mantieni aperto" lo impedisce.
    /// </summary>
    void WatchHover()
    {
        if (!IsVisible || OpenedBy != PanelOpening.Hover) { hoverWatch.Stop(); return; }
        Native.GetCursorPos(out var p);
        var hwnd = new WindowInteropHelper(this).Handle;
        var margin = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        var inside = Native.GetWindowRect(hwnd, out var r)
            && p.X >= r.Left - margin && p.X <= r.Right + margin && p.Y >= r.Top - margin && p.Y <= r.Bottom + margin;
        var nearIcon = Math.Abs(p.X - anchor.X) <= margin * 1.5 && Math.Abs(p.Y - anchor.Y) <= margin * 1.5;
        if (inside || nearIcon || KeepOpen.IsChecked == true) { hoverLeftAt = null; return; }
        hoverLeftAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - hoverLeftAt > TimeSpan.FromMilliseconds(600))
        {
            hoverWatch.Stop();
            Dismiss();
        }
    }

    /// <summary>Angolo della barra delle applicazioni dove stanno le icone di notifica, in pixel fisici.</summary>
    static Native.POINT? TrayCorner()
    {
        var bar = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref bar) == UIntPtr.Zero) return null;
        var rc = bar.rc;
        return (TaskbarEdge)bar.uEdge switch
        {
            TaskbarEdge.Top => new Native.POINT { X = rc.Right - 2, Y = rc.Top + 2 },
            TaskbarEdge.Left => new Native.POINT { X = rc.Left + 2, Y = rc.Bottom - 2 },
            _ => new Native.POINT { X = rc.Right - 2, Y = rc.Bottom - 2 },      // in basso o a destra: l'orologio è nell'angolo
        };
    }

    /// <summary>Larghezza e scala dalle Preferenze; la scala ingrandisce tutto il contenuto insieme alla finestra.</summary>
    public void ApplySize()
    {
        var s = app.Settings;
        // "clicca sull'icona per riaprire" serve solo quando il pannello non si apre già al passaggio
        MinimizeButton.ToolTip = L.T(s.OpenOnHover ? "MinimizeToTray" : "TrayHint");
        Width = s.PanelWidth * s.PanelScale;
        if (Content is FrameworkElement root)
            root.LayoutTransform = Math.Abs(s.PanelScale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s.PanelScale, s.PanelScale);
        if (IsVisible) QueuePlacement();
    }

    public void Update(PanelModel model, DateTimeOffset target)
    {
        KeepOpen.IsChecked = app.Settings.KeepPanelOpen;
        DataContext = app.Settings.ShowChart ? model : model with { Chart = null, ChartSummary = null };
        Chart.Target = model.ShowEveningMarker ? target : null;
        Chart.InvalidateVisual();
    }

    public void Dismiss()
    {
        if (!IsVisible || dismissing) return;
        hoverWatch.Stop();
        dismissing = true;
        HiddenAt = DateTime.UtcNow;
        IsHitTestVisible = false;
        int version = ++animationVersion;
        AnimateOpacity(0, () =>
        {
            if (version != animationVersion) return;
            Hide();
            dismissing = false;
            IsHitTestVisible = true;
        });
    }

    void AnimateOpacity(double to, Action? completed)
    {
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(Opacity, to, TimeSpan.FromMilliseconds(150))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) => completed?.Invoke();
        Opacity = to;
        BeginAnimation(OpacityProperty, animation);
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => Dismiss();

    public void ApplyFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = Native.DWMWCP_ROUND, dark = Theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Re-measure after WPF handles DPI / work-area / display changes.
        if (message is 0x02E0 or 0x001A or 0x007E) QueuePlacement();
        return IntPtr.Zero;
    }

    void QueuePlacement()
    {
        if (!anchored || !IsVisible || placementQueued) return;
        placementQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            placementQueued = false;
            if (IsVisible && !dismissing) PlaceAtAnchor();
        });
    }

    void PlaceAtAnchor()
    {
        var monitor = Native.MonitorFromPoint(anchor, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        var (work, full) = (info.rcWork, info.rcMonitor);
        MaxHeight = Math.Max(1, (work.Bottom - work.Top) / scale - 2 * Gap);
        MaxWidth = Math.Max(1, (work.Right - work.Left) / scale - 2 * Gap);
        UpdateLayout();
        // Read the actual HWND size, not a mixture of old-monitor DIPs and new-monitor DPI.
        if (!Native.GetWindowRect(hwnd, out var rect)) return;
        ScreenRect workRect = new(work.Left, work.Top, work.Right, work.Bottom), fullRect = new(full.Left, full.Top, full.Right, full.Bottom);
        var bar = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref bar) != UIntPtr.Zero)
            workRect = FlyoutPlacement.AvoidTaskbar(workRect, fullRect, new(bar.rc.Left, bar.rc.Top, bar.rc.Right, bar.rc.Bottom),
                (TaskbarEdge)bar.uEdge, (int)Math.Round(48 * scale));
        var bounds = FlyoutPlacement.Calculate(workRect, fullRect, anchor.X, anchor.Y,
            rect.Right - rect.Left, rect.Bottom - rect.Top, (int)Math.Round(Gap * scale));
        Native.SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }


    void Recalculate_Click(object sender, RoutedEventArgs e) => app.Coordinator?.RecalculateNow();

    void Retry_Click(object sender, RoutedEventArgs e) => app.Coordinator?.Retry();
    void Saver_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:batterysaver");
    void Power_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:powersleep");

    void History_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        app.ShowHistory();
    }

    void Prefs_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        app.ShowPreferences();
    }

    void Quit_Click(object sender, RoutedEventArgs e) => app.Quit();

    void OpenUri(string uri)
    {
        Dismiss();
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
