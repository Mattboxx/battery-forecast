using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryForecast.App;

/// <summary>
/// WPF scorre sempre di tre righe per ogni evento della rotellina, qualunque sia il Delta: i touchpad di
/// precisione mandano molti eventi piccoli e lo scorrimento diventa velocissimo. Qui lo spostamento è
/// proporzionale al Delta (una tacca da 120 = 48 px, come prima con la rotellina classica).
/// </summary>
static class WheelScrolling
{
    const double PixelsPerDelta = 48.0 / Mouse.MouseWheelDeltaForOneLine;

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // l'evento scende dall'esterno verso l'interno: lo prende solo il contenitore scorrevole più vicino al puntatore
        if (e.Handled || sv.CanContentScroll || Innermost(e.OriginalSource as DependencyObject, sv) != sv) return;
        sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset - e.Delta * PixelsPerDelta, 0, sv.ScrollableHeight));
        e.Handled = true;
    }

    static ScrollViewer? Innermost(DependencyObject? node, ScrollViewer outer)
    {
        for (; node is not null && node != outer; node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ScrollViewer { ScrollableHeight: > 0 } inner) return inner;
        return outer.ScrollableHeight > 0 ? outer : null;
    }
}
