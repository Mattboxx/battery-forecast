using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Core.Presentation;

public static class CalibrationPresenter
{
    public static string Present(bool enabled,CalibrationInfo? report,BlendInfo? blend=null)
    {
        if(!enabled) return L.T("CalibrationOff");
        // discharge curve from the forecast drain: it learns through the weight of the arrival at the reserve
        if(report?.Reason==LocalCalibration.DrainReason)
            return blend is { Evaluations: var n } && n>=EstimateBlend.MinimumEvaluations
                ? L.F("CalibrationArrivalsReady",n,Math.Round((1-blend.PowerWeight)*100).ToString("0",L.Culture))
                : L.F("CalibrationArrivalsCollecting",blend?.Evaluations??0,EstimateBlend.MinimumEvaluations);
        if(report?.Ready==true)
            return L.F("CalibrationReady",report.Windows,report.Sessions,report.Factor.ToString("0.00",L.Culture),
                L.T(report.Scope switch
                {
                    "same_apps_and_load"=>"CalibrationAppScope",
                    "similar_load"=>"CalibrationSimilarScope",
                    "all_use"=>"CalibrationAllScope",
                    _=>"CalibrationGeneralScope",      // same_load, and "general_load" saved by earlier versions
                }));
        if(report?.Reason=="fresh_load_needed") return L.T("CalibrationFreshLoad");
        return L.F("CalibrationCollecting",report?.Windows??0,LocalCalibration.MinimumWindows,report?.Sessions??0,LocalCalibration.MinimumSessions);
    }
}
