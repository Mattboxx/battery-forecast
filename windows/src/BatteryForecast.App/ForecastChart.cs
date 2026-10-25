using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
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
        typeof(ForecastView), typeof(ForecastChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target),
        typeof(DateTimeOffset?), typeof(ForecastChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ForecastView? Forecast { get => (ForecastView?)GetValue(ForecastProperty); set => SetValue(ForecastProperty, value); }
    public DateTimeOffset? Target { get => (DateTimeOffset?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    const double AxisRight = 34, AxisBottom = 16, Top = 4;

    Brush B(string key) => (TryFindResource(key) as Brush) ?? Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        var f = Forecast;
        if (f is null || f.Curve.Count == 0 || ActualWidth < 80) return;
        var w = ActualWidth - AxisRight;
        var h = ActualHeight - AxisBottom - Top;
        var t0 = f.Generated;
        var span = (f.HorizonEnd - t0).TotalSeconds;
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
            dc.DrawEllipse(B("Accent"), new Pen(B("CardBg"), 2), new Point(x, Y(at.P50)), 4.5, 4.5);
        }
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
        protected override bool IsContentElementCore() => true;
    }
}
