using BatteryForecast.Core.Telemetry;
using System.Runtime.InteropServices;

namespace BatteryForecast.Tests;

public class OptionalSensorTests
{
    [Fact]
    public void MissingProvidersAndAccessDeniedDoNotBlockBrightnessOrBattery()
    {
        var values=OptionalSensors.Read((ns,query,property)=>property switch
        {
            "Value"=>throw new COMException("Unavailable",unchecked((int)0x8004100E)),
            "CurrentTemperature"=>throw new UnauthorizedAccessException(),
            _=>[49]
        },()=>28);
        Assert.Null(values.CpuC); Assert.Null(values.ZoneC);
        Assert.Equal(49,values.Brightness); Assert.Equal(28,values.BatteryC);
        Assert.Equal(3,values.Errors.Count);
    }

    [Fact]
    public void DispatchFailureAndBatteryFailureAreObservedIndependently()
    {
        var values=OptionalSensors.Read((ns,query,property)=>property=="Value" ? [40] :
            property=="CurrentBrightness" ? throw new ArgumentException("Dispatch failed") : [3000],
            ()=>throw new InvalidOperationException());
        Assert.Equal(40,values.CpuC); Assert.Equal(26.9,values.ZoneC);
        Assert.Null(values.Brightness); Assert.Null(values.BatteryC);
        Assert.Contains("ArgumentException",values.Errors["brightness_pct"]);
        Assert.Contains("battery_temp_c",values.Errors.Keys);
    }

    [Fact]
    public void InvalidReadingsRemainUnavailableRatherThanZero()
    {
        var values=OptionalSensors.Read((ns,q,p)=>[double.NaN,double.PositiveInfinity,-1000,999999],()=>double.NaN);
        Assert.Null(values.CpuC); Assert.Null(values.BatteryC); Assert.Null(values.ZoneC); Assert.Null(values.Brightness);
    }
}
