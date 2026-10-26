using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.App;

/// <summary>Observed readings and an immutable issued forecast, with captions outside the plot.</summary>
sealed class ComparisonChart : FrameworkElement
{
    BatterySample[] actual = [];
    IReadOnlyList<DateTimeOffset> breaks = [];
    ForecastSnapshot? snapshot;
    ForecastView? forecast;
    bool showObserved = true, showPredicted = true;
    DateTimeOffset from, until, now;
    DateTimeOffset? hover, pinned;
    const double Top = 10, Right = 42, Bottom = 27;
    static readonly DependencyPropertyKey HoverTextKey = DependencyProperty.RegisterReadOnly(nameof(HoverText), typeof(string), typeof(ComparisonChart), new PropertyMetadata(null));
    static readonly DependencyPropertyKey PinnedTextKey = DependencyProperty.RegisterReadOnly(nameof(PinnedText), typeof(string), typeof(ComparisonChart), new PropertyMetadata(null));
    public static readonly DependencyProperty HoverTextProperty = HoverTextKey.DependencyProperty;
    public static readonly DependencyProperty PinnedTextProperty = PinnedTextKey.DependencyProperty;
    public string? HoverText => (string?)GetValue(HoverTextProperty);
    public string? PinnedText => (string?)GetValue(PinnedTextProperty);

    public ComparisonChart()
    {
        Height = 220;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        MouseMove += (_, e) =>
        {
            hover = Position(e.GetPosition(this));
            SetValue(HoverTextKey, hover is { } t && t != pinned ? Caption(t) : null);
            InvalidateVisual();
        };
        MouseLeave += (_, _) => { hover = null; SetValue(HoverTextKey, null); InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            pinned = hover = Position(e.GetPosition(this));
            SetValue(PinnedTextKey, pinned is { } t ? "◇ " + Caption(t) : null);
            SetValue(HoverTextKey, null);
            InvalidateVisual();
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearSelection(); };
        KeyDown += (_, e) =>
        {
            var current = hover ?? now;
            hover = e.Key switch { Key.Left => current.AddMinutes(-5), Key.Right => current.AddMinutes(5), Key.Home => from, Key.End => until, _ => null };
            if (hover is not { } t) return;
            hover = t < from ? from : t > until ? until : t;
            SetValue(HoverTextKey, Caption(hover.Value));
            InvalidateVisual();
            e.Handled = true;
        };
    }

    public void SetData(ForecastSnapshot? saved, IReadOnlyList<BatterySample> samples,
        IReadOnlyList<DateTimeOffset> interruptions, DateTimeOffset at)
    {
        bool sameForecast = snapshot?.Id == saved?.Id;
        snapshot = saved;
        forecast = saved?.View;
        actual = samples.OrderBy(s => s.TimestampUtc).ToArray();
        breaks = interruptions;
        now = at; from = at.AddHours(-12); until = at.AddHours(12);
        if (!sameForecast) ClearSelection();
        else
        {
            SetValue(HoverTextKey, hover is { } h && h != pinned ? Caption(h) : null);
            SetValue(PinnedTextKey, pinned is { } p ? "◇ " + Caption(p) : null);
            InvalidateVisual();
        }
        AutomationProperties.SetHelpText(this, L.T("TrendChartHelp"));
    }

    public void SetDisplay(bool observed, bool predicted)
    {
        if (showObserved == observed && showPredicted == predicted) return;
        showObserved = observed; showPredicted = predicted;
        ClearSelection();
    }

    public void ClearSelection()
    {
        hover = pinned = null;
        SetValue(HoverTextKey, null); SetValue(PinnedTextKey, null);
        InvalidateVisual();
    }

    BatterySample? ActualAt(DateTimeOffset time)
    {
        if (time > now || actual.Length == 0) return null;
        int lo = 0, hi = actual.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (actual[mid].TimestampUtc < time) lo = mid + 1; else hi = mid; }
        var nearest = actual[Math.Min(lo, actual.Length - 1)];
        if (lo > 0 && Math.Abs((actual[lo-1].TimestampUtc-time).TotalSeconds) < Math.Abs((nearest.TimestampUtc-time).TotalSeconds)) nearest = actual[lo-1];
        return nearest.LevelPct is >= 0 and <= 100 && Math.Abs((nearest.TimestampUtc-time).TotalSeconds) <= 90 ? nearest : null;
    }

    DateTimeOffset? Position(Point p)
    {
        double width = ActualWidth-Right;
        if ((!showObserved && !showPredicted) || width <= 0 || until <= from || p.X < 0 || p.X > width || p.Y < Top || p.Y > ActualHeight-Bottom) return null;
        var t = from.AddSeconds(p.X / width * (until-from).TotalSeconds);
        return (showObserved ? ActualAt(t)?.TimestampUtc : null) ?? t;
    }

    string Caption(DateTimeOffset t)
    {
        if (showObserved && ActualAt(t) is { } observed)
        {
            if (showPredicted && TrendComparison.At(snapshot, observed, actual, breaks) is { } compared) return TrendComparison.Describe(compared);
            return L.F("TrendObservedPoint", observed.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm", L.Culture), observed.LevelPct);
        }
        if (showPredicted && forecast?.At(t) is { } predicted)
            return L.F("TrendForecastPoint", t.ToLocalTime().ToString("dd/MM HH:mm", L.Culture), predicted.P50);
        return t.ToLocalTime().ToString("dd/MM HH:mm", L.Culture) + " · " + L.T("TrendNoPoint");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double width = ActualWidth-Right, height = ActualHeight-Bottom-Top;
        if (width < 20 || height < 20 || until <= from) return;
        Brush B(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        FormattedText Text(string s, string color = "TextTertiary") => new(s, L.Culture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, B(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double X(DateTimeOffset t) => (t-from).TotalSeconds / (until-from).TotalSeconds * width;
        double Y(double p) => Top + (100-Math.Clamp(p,0,100))/100*height;
        foreach (double v in new[] { 0.0, 25, 50, 75, 100 })
        {
            dc.DrawLine(new Pen(B("Grid"),1),new(0,Y(v)),new(width,Y(v)));
            dc.DrawText(Text($"{v:0}%"),new(width+6,Y(v)-7));
        }
        dc.PushClip(new RectangleGeometry(new Rect(0,Top,width,height)));
        if (showPredicted && forecast is { } f)
        {
            var points = new[] { new CurvePoint(f.Generated,f.Level,f.Level,f.Level) }.Concat(f.Curve).ToArray();
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.BeginFigure(new(X(points[0].T),Y(points[0].P90)),true,true);
                g.PolyLineTo(points.Skip(1).Select(p => new Point(X(p.T),Y(p.P90))).ToArray(),false,true);
                g.PolyLineTo(points.Reverse().Select(p => new Point(X(p.T),Y(p.P10))).ToArray(),false,true);
            }
            var fill = B("Curve").Clone(); fill.Opacity=.12;
            dc.DrawGeometry(fill,null,band);
            for (int i=1;i<points.Length;i++)
            {
                var pen = new Pen(B("Curve"),2);
                if (f.ExtrapolatedFrom is { } extra && points[i].T >= extra) pen.DashStyle=DashStyles.Dash;
                dc.DrawLine(pen,new(X(points[i-1].T),Y(points[i-1].P50)),new(X(points[i].T),Y(points[i].P50)));
            }
        }
        BatterySample? previous = null;
        if (showObserved) foreach (var sample in actual)
        {
            if (sample.LevelPct is not (>=0 and <=100)) { previous=null; continue; }
            if (previous is { LevelPct: double old } prev && sample.TimestampUtc-prev.TimestampUtc <= TimeSpan.FromMinutes(3) &&
                !breaks.Any(t => t > prev.TimestampUtc && t <= sample.TimestampUtc))
                dc.DrawLine(new Pen(B("Accent"),2.2),new(X(prev.TimestampUtc),Y(old)),new(X(sample.TimestampUtc),Y(sample.LevelPct.Value)));
            dc.DrawEllipse(B("Accent"),null,new(X(sample.TimestampUtc),Y(sample.LevelPct.Value)),1.1,1.1);
            previous=sample;
        }
        var guide = new Pen(B("TextTertiary"),1) { DashStyle=DashStyles.Dash };
        dc.DrawLine(guide,new(X(now),Top),new(X(now),Top+height));
        void Probe(DateTimeOffset t, bool fixedPoint)
        {
            var observed=showObserved ? ActualAt(t) : null;
            var predicted=showPredicted ? forecast?.At(t) : null;
            var level=observed?.LevelPct ?? predicted?.P50;
            if (level is null) return;
            var p=new Point(X(t),Y(level.Value));
            dc.DrawLine(guide,new(p.X,Top),new(p.X,Top+height));
            if (predicted is { } q) dc.DrawEllipse(B("Curve"),new Pen(B("CardBg"),1),new(X(t),Y(q.P50)),3,3);
            if (fixedPoint)
            {
                var diamond = new StreamGeometry();
                using(var g=diamond.Open())
                { g.BeginFigure(new(p.X,p.Y-4.5),false,true); g.PolyLineTo([new(p.X+4.5,p.Y),new(p.X,p.Y+4.5),new(p.X-4.5,p.Y)],true,true); }
                dc.DrawGeometry(null,new Pen(B("Text"),2),diamond);
                dc.DrawGeometry(null,new Pen(B("Accent"),1),diamond);
            }
            else dc.DrawEllipse(B(observed is null ? "Curve" : "Accent"),new Pen(B("CardBg"),1.5),p,3.5,3.5);
        }
        if (hover is { } moving) Probe(moving,false);
        if (pinned is { } selected) Probe(selected,true);
        dc.Pop();
        foreach(double k in new[] {0.0,.25,.5,.75,1.0})
        {
            var time=from.AddSeconds(k*(until-from).TotalSeconds).ToLocalTime();
            var label=Text(time.ToString("HH:mm",L.Culture));
            dc.DrawText(label,new(Math.Clamp(k*width-label.Width/2,0,Math.Max(0,width-label.Width)),Top+height+6));
        }
        if (!showObserved && !showPredicted)
            dc.DrawText(Text(L.T("TrendNoCurves")),new Point(10,Top+height/2));
        var nowLabel=Text(L.T("TrendNow"));
        dc.DrawText(nowLabel,new(Math.Clamp(X(now)-nowLabel.Width/2,0,Math.Max(0,width-nowLabel.Width)),Top+2));
    }
}
