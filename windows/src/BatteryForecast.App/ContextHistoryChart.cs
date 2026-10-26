using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

/// <summary>Observed five-minute means only; never join long or unavailable gaps.</summary>
sealed class ContextHistoryChart : FrameworkElement
{
    IReadOnlyList<ModelCovariate> rows = [];
    DateTimeOffset from, until;
    string unit = "%";
    bool percentage, display;
    string ValueText(double value) => display ? L.T("DisplayState_" + (int)value) : value.ToString("0.#", L.Culture) + " " + unit;
    string Caption(ModelCovariate p) => DateTimeOffset.FromUnixTimeSeconds(p.Ts).ToLocalTime().ToString("dd/MM HH:mm", L.Culture) + " · " + ValueText(p.Value!.Value);
    static readonly DependencyPropertyKey PinnedTextKey = DependencyProperty.RegisterReadOnly(nameof(PinnedText),
        typeof(string), typeof(ContextHistoryChart), new PropertyMetadata(null));
    public static readonly DependencyProperty PinnedTextProperty = PinnedTextKey.DependencyProperty;
    public string? PinnedText => (string?)GetValue(PinnedTextProperty);
    static readonly DependencyPropertyKey HoverTextKey = DependencyProperty.RegisterReadOnly(nameof(HoverText),
        typeof(string), typeof(ContextHistoryChart), new PropertyMetadata(null));
    public static readonly DependencyProperty HoverTextProperty = HoverTextKey.DependencyProperty;
    public string? HoverText => (string?)GetValue(HoverTextProperty);
    ModelCovariate? hover, pinned;
    public ContextHistoryChart()
    {
        Height = 145;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        MouseMove += (_, e) =>
        {
            hover = AtPosition(e.GetPosition(this));
            SetValue(HoverTextKey, hover is { Value: double v } point && (pinned is null || point.Ts != pinned.Value.Ts)
                ? Caption(point) : null);
            InvalidateVisual();
        };
        MouseLeave += (_, _) => { hover = null; SetValue(HoverTextKey, null); InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            pinned = hover = AtPosition(e.GetPosition(this));
            SetValue(PinnedTextKey, pinned is { Value: double v } point
                ? "◇ " + Caption(point) : null);
            SetValue(HoverTextKey, null);
            InvalidateVisual();
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearSelection(); };
        Unloaded += (_, _) => ClearSelection();
        KeyDown += (_, e) =>
        {
            var values = rows.Where(p => p.Value is double v && double.IsFinite(v)).ToArray();
            if (values.Length == 0) return;
            var i = hover is { } selected ? Array.FindIndex(values, p => p.Ts == selected.Ts) : -1;
            switch (e.Key)
            {
                case Key.Left: i = Math.Max(0, i - 1); break;
                case Key.Right: i = Math.Min(values.Length - 1, i + 1); break;
                case Key.Home: i = 0; break;
                case Key.End: i = values.Length - 1; break;
                default: return;
            }
            hover = values[i];
            SetValue(HoverTextKey, Caption(values[i]));
            InvalidateVisual();
            e.Handled = true;
        };
    }
    ModelCovariate? AtPosition(Point position)
    {
        var width = ActualWidth - 58;
        if (rows.Count == 0 || width <= 0 || until <= from || position.X < 0 || position.X > width ||
            position.Y < 5 || position.Y > ActualHeight - 28) return null;
        var time = from.AddSeconds(position.X / width * (until - from).TotalSeconds).ToUnixTimeSeconds();
        var nearest = rows.MinBy(p => Math.Abs(p.Ts - time));
        return nearest.Value is double v && double.IsFinite(v) && Math.Abs(nearest.Ts - time) <= 300 ? nearest : null;
    }
    public void ClearSelection()
    {
        pinned = hover = null;
        SetValue(PinnedTextKey, null);
        SetValue(HoverTextKey, null);
        InvalidateVisual();
    }
    public void SetData(IReadOnlyList<ModelCovariate> values, DateTimeOffset start, DateTimeOffset end, string units, bool fixedPercent = false, bool displayState = false)
    {
        ClearSelection();
        rows = values; from = start; until = end; unit = units; percentage = fixedPercent; display = displayState;
        AutomationProperties.SetHelpText(this, L.F("ContextChartSummary", rows.Count(p => p.Value is not null), units));
        InvalidateVisual();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        Brush B(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var face = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text) => new(text, L.Culture, FlowDirection.LeftToRight, face, 11, B("TextTertiary"), dpi);
        var valid = rows.Where(p => p.Value is double v && double.IsFinite(v)).ToArray();
        if (valid.Length == 0 || until <= from) { dc.DrawText(Text(L.T("ContextChartEmpty")), new Point(0, 20)); return; }
        double width = ActualWidth - 58, height = ActualHeight - 28;
        if (width < 20 || height < 20) return;
        double low = percentage ? 0 : Math.Min(0, valid.Min(p => p.Value!.Value));
        double high = display ? 2 : percentage ? 100 : Math.Max(1, valid.Max(p => p.Value!.Value));
        double X(long time) => Math.Clamp((time - from.ToUnixTimeSeconds()) / (until - from).TotalSeconds, 0, 1) * width;
        double Y(double v) => 5 + (high - v) / (high - low) * (height - 5);
        foreach (var value in new[] { low, (low + high) / 2, high })
        {
            var y = Y(value);
            dc.DrawLine(new Pen(B("Grid"), 1), new(0, y), new(width, y));
            dc.DrawText(Text(ValueText(value)), new Point(width + 5, y - 7));
        }
        var pen = new Pen(B("Curve"), 1.7);
        ModelCovariate? previous = null;
        foreach (var row in rows)
        {
            if (row.Value is not double v || !double.IsFinite(v)) { previous = null; continue; }
            var point = new Point(X(row.Ts), Y(v));
            if (previous is { Value: double old } prev && row.Ts - prev.Ts is > 0 and <= 600)
            {
                if (display)
                {
                    var corner = new Point(point.X, Y(old));
                    dc.DrawLine(pen, new Point(X(prev.Ts), Y(old)), corner);
                    dc.DrawLine(pen, corner, point);
                }
                else dc.DrawLine(pen, new Point(X(prev.Ts), Y(old)), point);
            }
            dc.DrawEllipse(B("Curve"), null, point, 1.7, 1.7);
            previous = row;
        }
        foreach (var k in new[] { 0.0, 0.5, 1.0 })
        {
            var time = from.AddSeconds((until - from).TotalSeconds * k).ToLocalTime();
            var text = Text(time.ToString((until - from).TotalHours > 24 ? "dd/MM HH:mm" : "HH:mm", L.Culture));
            dc.DrawText(text, new Point(Math.Clamp(k * width - text.Width / 2, 0, Math.Max(0, width - text.Width)), height + 6));
        }
        void Probe(ModelCovariate sample, bool fixedPoint, bool labels)
        {
            if (sample.Value is not double v) return;
            var point = new Point(X(sample.Ts), Y(v));
            var brush = B(fixedPoint ? "Accent" : "TextTertiary").Clone();
            brush.Opacity = fixedPoint || pinned is null ? 0.8 : 0.35;
            var guide = new Pen(brush, 1) { DashStyle = new DashStyle([2, 2], 0) };
            dc.DrawLine(guide, new Point(point.X, 5), new Point(point.X, height));
            dc.DrawLine(guide, new Point(0, point.Y), new Point(width, point.Y));
            if (fixedPoint)
            {
                var diamond = new StreamGeometry();
                using (var shape = diamond.Open())
                {
                    shape.BeginFigure(new Point(point.X, point.Y - 4.5), false, true);
                    shape.PolyLineTo([new(point.X + 4.5, point.Y), new(point.X, point.Y + 4.5), new(point.X - 4.5, point.Y)], true, true);
                }
                dc.DrawGeometry(null, new Pen(B("Text"), 2), diamond);
                dc.DrawGeometry(null, new Pen(B("Accent"), 1), diamond);
            }
            else dc.DrawEllipse(B("Curve"), new Pen(B("CardBg"), 1.5), point, 3.5, 3.5);
            if (!labels) return;
            var value = Text(ValueText(v));
            var y = Math.Clamp(point.Y - value.Height / 2, 0, Math.Max(0, height - value.Height));
            dc.DrawRectangle(B("CardBg"), null, new Rect(width + 2, y, 56, value.Height));
            dc.DrawText(value, new Point(width + 5, y));
            var time = Text(DateTimeOffset.FromUnixTimeSeconds(sample.Ts).ToLocalTime().ToString("dd/MM HH:mm", L.Culture));
            var x = Math.Clamp(point.X - time.Width / 2, 0, Math.Max(0, width - time.Width));
            dc.DrawRectangle(B("CardBg"), null, new Rect(x, height + 6, time.Width, time.Height));
            dc.DrawText(time, new Point(x, height + 6));
        }
        if (hover is { } moving) Probe(moving, false, pinned is null);
        if (pinned is { } fixedPoint) Probe(fixedPoint, true, true);
        var selected = pinned ?? hover;
        AutomationProperties.SetItemStatus(this, selected is { Value: double number } p
            ? $"{DateTimeOffset.FromUnixTimeSeconds(p.Ts).ToLocalTime():g}: {number:0.#} {unit}" : "");

    }
}
