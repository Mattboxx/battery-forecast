using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

/// <summary>
/// Le prossime 24 ore: fascia dalla stima prudente a quella ottimista, stima centrale, soglia di riserva
/// e obiettivo serale. Disegno vettoriale semplice, nessuna libreria. Il riassunto testuale per gli
/// screen reader arriva da AutomationProperties.HelpText.
/// </summary>
sealed class ForecastChart : FrameworkElement
{
    public static readonly DependencyProperty ForecastProperty = DependencyProperty.Register(nameof(Forecast),
        typeof(ForecastView), typeof(ForecastChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) => ((ForecastChart)owner).RefreshProbe()));
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target),
        typeof(DateTimeOffset?), typeof(ForecastChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ForecastView? Forecast { get => (ForecastView?)GetValue(ForecastProperty); set => SetValue(ForecastProperty, value); }
    public DateTimeOffset? Target { get => (DateTimeOffset?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    double? selection;
    string selectionText = "";
    readonly ToolTip probeTip;

    public ForecastChart()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        probeTip = new ToolTip { PlacementTarget = this, Placement = PlacementMode.Relative, StaysOpen = true };
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            var width = ActualWidth - AxisRight;
            if (width > 0 && p.X >= 0 && p.X <= width && p.Y >= Top && p.Y <= ActualHeight - AxisBottom)
                Select(p.X / width);
            else ClearSelection();
        };
        MouseLeftButtonDown += (_, _) => Focus();
        MouseLeave += (_, _) => { if (!IsKeyboardFocusWithin) ClearSelection(); else probeTip.IsOpen = false; };
        LostKeyboardFocus += (_, _) => ClearSelection();
        Unloaded += (_, _) => ClearSelection();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearSelection(); };
        KeyDown += (_, e) =>
        {
            if (Forecast is not { } f || f.HorizonEnd <= f.Generated) return;
            double step = 300 / (f.HorizonEnd - f.Generated).TotalSeconds;
            switch (e.Key)
            {
                case Key.Left: Select((selection ?? 0) - step); break;
                case Key.Right: Select((selection ?? 0) + step); break;
                case Key.Home: Select(0); break;
                case Key.End: Select(1); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    void RefreshProbe()
    {
        if (selection is double fraction && IsVisible) Select(fraction);
        else ClearSelection();
    }

    void Select(double fraction)
    {
        selection = Math.Clamp(fraction, 0, 1);
        if (ChartSelection.AtFraction(Forecast, selection.Value) is not { } point) { ClearSelection(); return; }
        var previous = selectionText;
        var time = point.T.ToLocalTime();
        selectionText = L.F("ChartProbe", time.ToString("ddd dd MMM HH:mm zzz", L.Culture), Math.Round(point.P50), Math.Round(point.P10), Math.Round(point.P90));
        if (Forecast?.ExtrapolatedFrom is { } start && point.T >= start) selectionText += Environment.NewLine + L.T("ChartExtrapolated");
        probeTip.Content = selectionText;
        probeTip.HorizontalOffset = Math.Clamp(selection.Value * (ActualWidth - AxisRight) - 100, 0, Math.Max(0, ActualWidth - 240));
        probeTip.VerticalOffset = ActualHeight + 4;
        probeTip.IsOpen = true;
        if (previous != selectionText && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, previous, selectionText);
        InvalidateVisual();
    }

    void ClearSelection()
    {
        selection = null;
        selectionText = "";
        probeTip.IsOpen = false;
        InvalidateVisual();
    }

    const double AxisRight = 34, AxisBottom = 16, Top = 4;

    Brush B(string key) => (TryFindResource(key) as Brush) ?? Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var f = Forecast;
        if (f is null || f.Curve.Count == 0 || ActualWidth < 80 || ActualHeight <= Top + AxisBottom) return;
        var w = ActualWidth - AxisRight;
        var h = ActualHeight - AxisBottom - Top;
        var t0 = f.Generated;
        var span = (f.HorizonEnd - t0).TotalSeconds;
        if (span <= 0) return;
        double X(DateTimeOffset t) => (t - t0).TotalSeconds / span * w;
        double Y(double v) => Top + (1 - Math.Clamp(v, 0, 100) / 100) * h;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Label(string s, Brush b) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, b, dpi);

        // griglia e asse delle percentuali
        var grid = new Pen(B("Grid"), 1);
        foreach (var v in new[] { 0, 50, 100 })
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            var text = Label($"{v}%", B("TextTertiary"));
            dc.DrawText(text, new Point(w + 6, y - text.Height / 2));
        }

        // ore tonde locali multiple di 6
        var zone = TimeZoneInfo.Local;
        var first = TimeZoneInfo.ConvertTime(t0, zone);
        var tick = new DateTimeOffset(first.Year, first.Month, first.Day, first.Hour, 0, 0, first.Offset).AddHours(1);
        for (; tick < f.HorizonEnd; tick = tick.AddHours(1))
        {
            var local = TimeZoneInfo.ConvertTime(tick, zone);
            if (local.Hour % 6 != 0) continue;
            var x = X(tick);
            var text = Label(local.ToString("t", CultureInfo.CurrentCulture), B("TextTertiary"));
            if (x - text.Width / 2 < 0 || x + text.Width / 2 > w) continue;
            dc.DrawLine(grid, new Point(x, Top + h), new Point(x, Top + h + 3));
            dc.DrawText(text, new Point(x - text.Width / 2, Top + h + 3));
        }

        // fascia e stima centrale, a partire dal livello attuale
        var pts = new List<CurvePoint>(f.Curve.Count + 1) { new(t0, f.Level, f.Level, f.Level) };
        pts.AddRange(f.Curve);
        var band = new StreamGeometry();
        using (var g = band.Open())
        {
            g.BeginFigure(new Point(X(pts[0].T), Y(pts[0].P90)), true, true);
            g.PolyLineTo([.. pts.Skip(1).Select(p => new Point(X(p.T), Y(p.P90)))], false, true);
            g.PolyLineTo([.. pts.AsEnumerable().Reverse().Select(p => new Point(X(p.T), Y(p.P10)))], false, true);
        }
        band.Freeze();
        var bandColor = TryFindResource("BandColor") is Color c ? c : Colors.Gray;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, bandColor.R, bandColor.G, bandColor.B),
            Color.FromArgb(0x10, bandColor.R, bandColor.G, bandColor.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, band);

        var reserve = new Pen(B("Reserve"), 1) { DashStyle = new DashStyle([3, 3], 0) };
        dc.DrawLine(reserve, new Point(0, Y(f.ReservePct)), new Point(w, Y(f.ReservePct)));

        // stima centrale: continua finché viene dalle abitudini, tratteggiata dove è estrapolata
        var curve = B("Curve");
        var split = f.ExtrapolatedFrom is { } ex ? pts.FindIndex(p => p.T >= ex) : -1;
        var solid = split > 0 ? pts[..(split + 1)] : pts;
        dc.DrawGeometry(null, new Pen(curve, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            Polyline(solid, X, Y));
        if (split > 0)
            dc.DrawGeometry(null, new Pen(curve, 2) { DashStyle = new DashStyle([1.5, 2], 0), DashCap = PenLineCap.Round },
                Polyline(pts[split..], X, Y));
        var halo = curve.Clone();
        halo.Opacity = 0.25;
        dc.DrawEllipse(halo, null, new Point(X(t0), Y(f.Level)), 7, 7);
        dc.DrawEllipse(curve, null, new Point(X(t0), Y(f.Level)), 3.5, 3.5);

        // obiettivo serale
        if (Target is { } target && f.At(target) is { } at)
        {
            var x = X(target);
            dc.DrawLine(new Pen(B("TextTertiary"), 1) { DashStyle = new DashStyle([2, 3], 0) }, new Point(x, Top), new Point(x, Top + h));
            if (selection is null) dc.DrawEllipse(B("Accent"), new Pen(B("CardBg"), 2), new Point(x, Y(at.P50)), 4.5, 4.5);
        }

        if (selection is double fraction && ChartSelection.AtFraction(f, fraction) is { } probe)
        {
            var point = new Point(X(probe.T), Y(probe.P50));
            var guide = new Pen(B("TextTertiary"), 1) { DashStyle = new DashStyle([2, 2], 0) };
            dc.DrawLine(guide, new Point(point.X, Top), new Point(point.X, Top + h));
            dc.DrawLine(guide, new Point(0, point.Y), new Point(w, point.Y));
            dc.DrawEllipse(B("Accent"), new Pen(B("CardBg"), 2), point, 5, 5);
            var percent = Label($"{probe.P50:0}%", B("Accent"));
            var labelY = Math.Clamp(point.Y - percent.Height / 2, 0, Math.Max(0, ActualHeight - percent.Height));
            dc.DrawRectangle(B("CardBg"), null, new Rect(w + 2, labelY, AxisRight - 2, percent.Height));
            dc.DrawText(percent, new Point(w + 6, labelY));
            var clock = Label(probe.T.ToLocalTime().ToString("HH:mm", L.Culture), B("Accent"));
            var labelX = Math.Clamp(point.X - clock.Width / 2, 0, Math.Max(0, w-clock.Width));
            dc.DrawRectangle(B("CardBg"), null, new Rect(labelX, Top+h+3, clock.Width, clock.Height));
            dc.DrawText(clock, new Point(labelX, Top+h+3));
        }
        if (IsKeyboardFocused) dc.DrawRectangle(null, new Pen(B("Accent"), 1), new Rect(0,0,ActualWidth,ActualHeight));
    }

    static StreamGeometry Polyline(List<CurvePoint> pts, Func<DateTimeOffset, double> x, Func<double, double> y)
    {
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(x(pts[0].T), y(pts[0].P50)), false, false);
            g.PolyLineTo([.. pts.Skip(1).Select(p => new Point(x(p.T), y(p.P50)))], true, true);
        }
        line.Freeze();
        return line;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

    sealed class ChartPeer(ForecastChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetNameCore() => L.T("ChartName");
        protected override string GetItemStatusCore() => owner.selectionText;
        protected override bool IsContentElementCore() => true;
    }
}
