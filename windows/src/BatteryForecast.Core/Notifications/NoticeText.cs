using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.Core.Notifications;

public static class NoticeText
{
    /// <summary>Titolo e testo localizzati di un avviso.</summary>
    public static (string Title, string Body) For(BatteryNotice n, double reservePct) => n.Rule switch
    {
        "low" => (L.F("NoticeLowTitle", PanelPresenter.Pct(n.Value)), L.T("NoticeLow")),
        "charged" => (L.F("NoticeChargedTitle", PanelPresenter.Pct(n.Value)), L.T("NoticeCharged")),
        "drain" => (L.T("NoticeDrainTitle"), L.T("NoticeDrain")),
        _ => (L.F("NoticeMinutesTitle", PanelPresenter.Duration(TimeSpan.FromMinutes(n.Value)), PanelPresenter.Pct(reservePct)),
              L.T(n.Source == "habits" ? "NoticeMinutesHabits" : "NoticeMinutesRecent")),
    };

    /// <summary>
    /// Dallo stato del pannello all'ingresso delle regole. Conta solo una stima attuale: la previsione sulle
    /// abitudini se fresca e con storico sufficiente, altrimenti il consumo recente.
    /// </summary>
    public static (DateTimeOffset? ReserveAt, bool Fresh, string Source) Estimate(DashboardState s)
    {
        // il modello gira al massimo ogni 30 minuti: per gli avvisi basta una previsione recente e coerente
        if (s.Forecast is { Quality: not HistoryQuality.Insufficient } f && (s.ForecastFresh ||
            (s.Now - f.Generated < TimeSpan.FromMinutes(40) && s.Battery.LevelPct is double l && Math.Abs(l - f.Level) <= 3)))
            return (f.Crossing == CrossingStatus.WithinHorizon ? f.Central : null, true, "habits");
        if (s.Recent?.ReserveAt is { } r)
            return (r, true, "recent");
        return (null, false, "");
    }
}
