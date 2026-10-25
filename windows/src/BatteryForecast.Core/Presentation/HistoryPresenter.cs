using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Presentation;

public sealed record SegmentRow(string Day, string Change, string Rate, string? Energy);

public sealed record HistoryModel(
    string? HealthValue, string HealthDetail, string? HealthNote,
    IReadOnlyList<SegmentRow> Segments, string Coverage);

/// <summary>Testi della finestra "Storico e salute". Nessun dato inventato: ciò che manca si dice.</summary>
public static class HistoryPresenter
{
    const int MaxRows = 30;

    public static HistoryModel Present(IReadOnlyList<BatterySample> samples, IReadOnlyList<DateTimeOffset> interruptions,
        BatterySample? current, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        string? value = null, note = null;
        string detail;
        // la lettura più recente con entrambe le capacità, anche se quella attuale non le ha
        var withHealth = current is { HealthPct: not null } ? current : samples.LastOrDefault(s => s.HealthPct is not null);
        if (withHealth is { HealthPct: double h, FullChargeMWh: int full, DesignMWh: int design })
        {
            value = L.F("HealthValue", Math.Round(h));
            detail = L.F("HealthDetail", Wh(full), Wh(design),
                TimeZoneInfo.ConvertTime(withHealth.TimestampUtc, zone).ToString("g", L.Culture));
            if (h > 100.5) note = L.T("HealthOver");
        }
        else detail = L.T("HealthUnknown");

        var rows = HistorySummary.Segments(samples, interruptions).Take(MaxRows).Select(seg =>
        {
            var hours = seg.Duration.TotalHours;
            var rate = hours > 0 ? (seg.StartPct - seg.EndPct) / hours : 0;
            var start = TimeZoneInfo.ConvertTime(seg.Start, zone);
            return new SegmentRow(
                // "t" dentro un formato personalizzato è l'indicatore AM/PM: data e ora si formattano a parte
                $"{start.ToString("ddd d MMM", L.Culture)}, {start.ToString("t", L.Culture)}",
                L.F("SegmentRow", PanelPresenter.Pct(seg.StartPct), PanelPresenter.Pct(seg.EndPct), PanelPresenter.Duration(seg.Duration)),
                L.F("SegmentRate", rate.ToString("0.0", L.Culture)),
                seg.EnergyWh is double wh ? L.F("SegmentEnergy", wh.ToString("0.0", L.Culture)) : null);
        }).ToList();

        var days = samples.Count > 1 ? (samples[^1].TimestampUtc - samples[0].TimestampUtc).TotalDays : 0;
        return new HistoryModel(value, detail, note, rows, L.F("Coverage", PanelPresenter.Days(days), samples.Count));
    }

    static string Wh(int mwh) => (mwh / 1000.0).ToString("0.#", L.Culture);
}
