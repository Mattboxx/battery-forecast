using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Core.Presentation;

public static class ChartSelection
{
    /// <summary>Clamp to the plotted horizon, snap to the model's five-minute steps, interpolate quantiles.</summary>
    public static CurvePoint? AtFraction(ForecastView? forecast, double fraction)
    {
        if (forecast is null || forecast.Curve.Count == 0 || !double.IsFinite(fraction)) return null;
        var seconds = (forecast.HorizonEnd - forecast.Generated).TotalSeconds;
        if (seconds <= 0) return null;
        var offset = Math.Clamp(Math.Round(Math.Clamp(fraction, 0, 1) * seconds / 300) * 300, 0, seconds);
        return forecast.At(forecast.Generated.AddSeconds(offset));
    }
}
