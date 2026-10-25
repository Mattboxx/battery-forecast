using BatteryForecast.Core.Presentation;

namespace BatteryForecast.Tests;

public class FlyoutPlacementTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 1700, 1060, 384, 600, 12, 1508, 428)]
    [InlineData(0, 0, 1920, 1040, 1700, 1060, 384, 800, 12, 1508, 228)]
    [InlineData(0, 40, 1920, 1080, 1700, 20, 384, 600, 12, 1508, 52)]
    [InlineData(40, 0, 1920, 1080, 20, 900, 384, 600, 12, 52, 468)]
    [InlineData(0, 0, 1880, 1080, 1900, 900, 384, 600, 12, 1484, 468)]
    public void FollowsTaskbarAndReanchorsWhenContentGrows(int l, int t, int r, int b,
        int ax, int ay, int width, int height, int gap, int x, int y)
    {
        var result = FlyoutPlacement.Calculate(new(l,t,r,b), new(0,0,1920,1080), ax, ay, width, height, gap);
        Assert.Equal(new FlyoutBounds(x,y,width,height), result);
    }

    [Fact]
    public void NegativeMonitorOriginAndPhysicalDpiDimensionsStayInsideWorkArea()
    {
        var result = FlyoutPlacement.Calculate(new(-2560,-200,0,1180), new(-2560,-200,0,1240),
            -70,1200,576,900,18);
        Assert.Equal(new FlyoutBounds(-594,262,576,900), result);
    }

    [Fact]
    public void AutoHiddenBottomTaskbarKeepsThePanelAboveIt()
    {
        // 200%: schermo 2880x1800, barra nascosta (area di lavoro = schermo), Windows riporta la barra intera
        var full = new ScreenRect(0, 0, 2880, 1800);
        var work = FlyoutPlacement.AvoidTaskbar(full, full, new(0, 1704, 2880, 1800), TaskbarEdge.Bottom, 96);
        Assert.Equal(1704, work.Bottom);
        var r = FlyoutPlacement.Calculate(work, full, 2700, 1790, 768, 1400, 28);
        Assert.True(r.Y + r.Height <= 1704 - 28);
        // se riporta solo la striscia nascosta di 2 pixel si usa lo spessore tipico
        Assert.Equal(1704, FlyoutPlacement.AvoidTaskbar(full, full, new(0, 1798, 2880, 1800), TaskbarEdge.Bottom, 96).Bottom);
    }

    [Fact]
    public void TaskbarOnAnotherMonitorIsIgnored()
    {
        var full = new ScreenRect(0, 0, 1920, 1080);
        Assert.Equal(full, FlyoutPlacement.AvoidTaskbar(full, full, new(-1920, 1032, 0, 1080), TaskbarEdge.Bottom, 48));
    }

    [Fact]
    public void OversizedPanelIsLimitedToUsableArea()
    {
        var result = FlyoutPlacement.Calculate(new(0,0,800,560),new(0,0,800,600),790,580,900,1200,12);
        Assert.Equal(new FlyoutBounds(12,12,776,536),result);
    }
}
