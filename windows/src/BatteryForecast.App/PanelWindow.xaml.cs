using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

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
        SourceInitialized += (_, _) =>
        {
            ApplyFrame();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        };
        SizeChanged += (_, _) => QueuePlacement();
        Deactivated += (_, _) => { if (KeepOpen.IsChecked != true) Dismiss(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
        };
    }

    /// <summary>Quando il pannello si è nascosto perdendo il focus: un clic sull'icona subito dopo non deve riaprirlo.</summary>
    public DateTime HiddenAt { get; private set; }

    public void Show(PanelModel model, DateTimeOffset target)
    {
        Update(model, target);
        ++animationVersion;
        dismissing = false;
        IsHitTestVisible = true;
        BeginAnimation(OpacityProperty, null);
        if (IsVisible) { Opacity = 1; Activate(); return; }
        Native.GetCursorPos(out anchor);
        anchored = true;
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
        Activate();
        Native.SetForegroundWindow(hwnd);
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    public void Update(PanelModel model, DateTimeOffset target)
    {
        DataContext = model;
        Chart.Target = target;
        Chart.InvalidateVisual();
    }

    public void Dismiss()
    {
        if (!IsVisible || dismissing) return;
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
