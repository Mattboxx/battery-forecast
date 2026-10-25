using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.Tests;
public class ChartSelectionTests
{
    static readonly DateTimeOffset Start = new(2026,10,25,23,0,0,TimeSpan.Zero);
    static readonly ForecastView View = new(Start,80,5,CrossingStatus.WithinHorizon,null,null,null,
        [new(Start.AddHours(1),50,60,70),new(Start.AddHours(2),20,40,60)],HistoryQuality.Ok,7,7,"test","test");

    [Fact]
    public void BothAxesUseTheSameInterpolatedCurvePoint()
    {
        var point = ChartSelection.AtFraction(View,0.25)!.Value;
        Assert.Equal(Start.AddMinutes(30),point.T);
        Assert.Equal(70,point.P50); Assert.Equal(65,point.P10); Assert.Equal(75,point.P90);
    }
    [Fact]
    public void SelectionClampsAndPreservesTheNextDate()
    {
        Assert.Equal(80,ChartSelection.AtFraction(View,-1)!.Value.P50);
        var end = ChartSelection.AtFraction(View,2)!.Value;
        Assert.Equal(Start.AddHours(2),end.T); Assert.Equal(40,end.P50);
        Assert.Equal(26,end.T.Day);
    }
    [Fact]
    public void MissingOrInvalidInputsDoNotInventValues()
    {
        Assert.Null(ChartSelection.AtFraction(null,0.5));
        Assert.Null(ChartSelection.AtFraction(View,double.NaN));
        Assert.Null(ChartSelection.AtFraction(View with { Curve=[] },0.5));
    }
}
