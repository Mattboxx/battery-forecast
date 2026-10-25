using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BatteryForecast.App;

/// <summary>Nascosto se il valore è null, stringa vuota o false.</summary>
sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null or "" or false ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

sealed class BoolToVisible : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Indicatore di attività: un arco che ruota. Fermo se le animazioni di sistema sono disattivate.</summary>
sealed class Spinner : FrameworkElementHost
{
    public Spinner()
    {
        var arc = new Path
        {
            Data = Geometry.Parse("M 6,1 A 5,5 0 1 1 1,6"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "TextTertiary");
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        Child = arc;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && SystemParameters.ClientAreaAnimation)
                rotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        System.Windows.Automation.AutomationProperties.SetName(this, "…");
    }
}

/// <summary>Contenitore minimo con un solo figlio visuale.</summary>
class FrameworkElementHost : FrameworkElement
{
    UIElement? child;
    public UIElement? Child
    {
        get => child;
        set
        {
            if (child is not null) { RemoveVisualChild(child); RemoveLogicalChild(child); }
            child = value;
            if (child is not null) { AddVisualChild(child); AddLogicalChild(child); }
            InvalidateMeasure();
        }
    }
    protected override int VisualChildrenCount => child is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => child!;
    protected override Size MeasureOverride(Size s) { child?.Measure(s); return child?.DesiredSize ?? default; }
    protected override Size ArrangeOverride(Size s) { child?.Arrange(new Rect(s)); return s; }
}
