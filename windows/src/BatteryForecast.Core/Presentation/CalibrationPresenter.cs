using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Core.Presentation;

public static class CalibrationPresenter
{
    public static string Present(bool enabled,CalibrationInfo? report)
    {
        if(!enabled) return L.T("CalibrationOff");
        if(report?.Ready==true)
            return L.F("CalibrationReady",report.Windows,report.Sessions,report.Factor.ToString("0.00",L.Culture),
                L.T(report.Scope=="same_apps_and_load"?"CalibrationAppScope":"CalibrationGeneralScope"));
        if(report?.Reason=="fresh_load_needed") return L.T("CalibrationFreshLoad");
        return L.F("CalibrationCollecting",report?.Windows??0,LocalCalibration.MinimumWindows,report?.Sessions??0,LocalCalibration.MinimumSessions);
    }
}
