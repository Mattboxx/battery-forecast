using BatteryForecast.Core.Presentation;

namespace BatteryForecast.Tests;

public class TimeViewportTests
{
    [Fact]
    public void ZoomPreservesTimeUnderPointerAndBoundsScale()
    {
        var start=DateTimeOffset.UnixEpoch;var view=new TimeViewport(TimeSpan.FromMinutes(15));
        view.SetBounds(start,start.AddHours(24));
        var anchor=start.AddHours(6);view.Zoom(2,.25);
        Assert.Equal(anchor,view.From.AddHours(3));Assert.Equal(TimeSpan.FromHours(12),view.Until-view.From);
        view.Zoom(1e12);Assert.Equal(TimeSpan.FromMinutes(15),view.Until-view.From);
        view.Zoom(1e-12);Assert.Equal(start,view.From);Assert.Equal(start.AddHours(24),view.Until);
    }
    [Fact]
    public void PanAndRefreshPreserveTheZoomedRangeWithinAvailableHistory()
    {
        var start=DateTimeOffset.UnixEpoch;var view=new TimeViewport(TimeSpan.FromMinutes(15));
        view.SetBounds(start,start.AddHours(24));view.Zoom(4);
        view.Pan(100);Assert.Equal(start.AddHours(24),view.Until);
        view.SetBounds(start.AddMinutes(1),start.AddHours(24).AddMinutes(1));
        Assert.Equal(TimeSpan.FromHours(6),view.Until-view.From);
        view.Pan(-100);view.Pan(-100);Assert.Equal(view.FullFrom,view.From);
        view.Reset();Assert.False(view.IsZoomed);
    }
}
