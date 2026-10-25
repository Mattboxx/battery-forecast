using System.Globalization;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Core.Presentation;

/// <summary>Da dove viene il numero principale del pannello. La UI lo dice sempre all'utente.</summary>
public enum EstimateSource { None, Habits, Recent }

/// <summary>Tutti i testi e i flag del pannello per uno stato. Nessuna logica nella vista.</summary>
public sealed record PanelModel
{
    public string Level { get; init; } = "";
    public string PowerLine { get; init; } = "";
    public string? SaverLine { get; init; }
    public string? DemoLine { get; init; }
    public string SectionTitle { get; init; } = "";
    public string? UpdatedAt { get; init; }
    public bool Busy { get; init; }
    public string Headline { get; init; } = "";
    public string? Detail { get; init; }
    public string? Note { get; init; }
    public EstimateSource Source { get; init; }
    public string? SourceLine { get; init; }
    public string? RechargeLine { get; init; }
    public string? RecentLine { get; init; }
    public string? WindowsLine { get; init; }
    public bool Stale { get; init; }
    public bool CanRetry { get; init; }
    public string? TargetLabel { get; init; }
    public string? TargetValue { get; init; }
    public string? TargetRange { get; init; }
    public ForecastView? Chart { get; init; }
    public string? ChartSummary { get; init; }
    public string? SavingTitle { get; init; }
    public string? SavingDetail { get; init; }
    public string Tooltip { get; init; } = "Battery Forecast";
}

public static class PanelPresenter
{
    const int HorizonHours = 24;

    public static PanelModel Present(DashboardState s, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var b = s.Battery;
        var level = b.LevelPct is double l ? $"{l:0}%" : "—";
        var m = new PanelModel
        {
            Level = level,
            PowerLine = L.F("PowerSource", L.T(b.OnAc == true ? "SourceAc" : "SourceBattery")),
            SaverLine = b.BatterySaver == true ? L.T("SaverOn") : null,
            DemoLine = s.Demo ? L.T("Demo") : null,
            SectionTitle = L.T("ExpectedRuntime"),
        };

        switch (s.Phase)
        {
            case PowerPhase.NoBattery:
                return m with { Level = "", PowerLine = L.F("PowerSource", L.T("SourceAc")), Headline = L.T("NoBattery"),
                    Detail = L.T("NoBatteryDetail"), Tooltip = "Battery Forecast · " + L.T("NoBattery") };
            case PowerPhase.SensorUnavailable:
                return m with { Headline = L.T("SensorUnavailable"), Detail = L.T("SensorUnavailableDetail"),
                    Tooltip = "Battery Forecast · " + L.T("SensorUnavailable") };
            case PowerPhase.Charging or PowerPhase.PluggedIn:
                var full = b.LevelPct >= 100;
                var head = full ? L.T("FullyCharged") : s.Phase == PowerPhase.Charging ? L.F("ChargingTo", Pct(b.LevelPct)) : L.T("PluggedIn");
                return m with
                {
                    Headline = head,
                    Note = L.T("ResumesOnUnplug"),
                    Tooltip = Tip($"Battery Forecast · {level}", L.T(s.Phase == PowerPhase.Charging && !full ? "TipCharging" : "TipPlugged")),
                };
        }

        // a batteria
        var now = s.Now;
        var f = s.Forecast;
        var recent = s.Recent;
        var habitsUsable = f is not null && f.Quality != HistoryQuality.Insufficient;
        m = m with
        {
            UpdatedAt = f is not null ? L.F("UpdatedAt", Clock(f.Generated, zone)) : null,
            Busy = s.Updating,
            Stale = f is not null && !s.ForecastFresh,
            WindowsLine = b.OsRemainingSeconds is int os && os > 0 ? L.F("WindowsLine", Hm(TimeSpan.FromSeconds(os))) : null,
        };

        if (habitsUsable)
        {
            m = HabitHeadline(m, f!, s, zone);
            if (recent?.ReserveAt is { } ra)
                m = m with { RecentLine = L.F("RecentLine", Duration(ra - now), UntilPhrase(ra, now, zone)) };
        }
        else if (recent?.ReserveAt is { } ra)
        {
            m = m with
            {
                Source = EstimateSource.Recent,
                Headline = Until(ra, now, zone),
                Detail = L.F("About", Duration(ra - now)),
                Note = L.F("ToReserveShort", Pct(s.ReservePct)),
                SourceLine = L.T("SourceRecentOnly"),
            };
        }
        else
        {
            m = m with { Headline = s.Updating ? L.T(s.Engine == EngineState.Loading ? "EngineLoading" : "Calculating") : L.T("Collecting"),
                Detail = L.T("CollectingDetail") };
        }

        if (f is null && !s.Updating && s.EngineError is { } err)
            m = m with
            {
                Note = L.T(err switch
                {
                    "not_found" or "model_unavailable" => "EngineMissing",
                    "timeout" => "EngineSlow",
                    "backoff" => "EnginePaused",
                    _ => "EngineStopped",
                }),
                CanRetry = err is not ("not_found" or "model_unavailable"),
            };

        if (f is not null && habitsUsable)
        {
            if (s.AtTarget is { } below && below.P50 <= f.ReservePct)
                m = m with
                {
                    TargetLabel = L.F(s.Target.Tomorrow ? "AtTargetTomorrow" : "AtTarget", Clock(s.Target.When, zone)),
                    TargetRange = L.F("TargetBelowReserve", Pct(f.ReservePct)),
                };
            else if (s.AtTarget is { } p)
                m = m with
                {
                    TargetLabel = L.F(s.Target.Tomorrow ? "AtTargetTomorrow" : "AtTarget", Clock(s.Target.When, zone)),
                    TargetValue = $"{Math.Round(p.P50):0}%",
                    TargetRange = L.F("TargetRange", Math.Round(p.P10), Math.Round(p.P90)),
                };
            else
                m = m with
                {
                    TargetLabel = L.F(s.Target.Tomorrow ? "AtTargetTomorrow" : "AtTarget", Clock(s.Target.When, zone)),
                    TargetRange = L.T("TargetBeyond"),
                };
            m = m with { Chart = f, ChartSummary = Summary(f, now) };
        }

        if (s.Saving != SavingSuggestion.None && s.SavingAt is { } at)
            m = m with
            {
                SavingTitle = s.Saving == SavingSuggestion.Likely ? L.F("SavingLikelyTitle", Clock(s.Target.When, zone)) : L.T("SavingPossibleTitle"),
                SavingDetail = s.Saving == SavingSuggestion.Likely ? L.F("SavingLikely", UntilPhrase(at, now, zone)) : L.F("SavingPossible", At(at, now, zone)),
            };

        return m with { Tooltip = Tooltip(m, level, s, zone) };
    }

    static PanelModel HabitHeadline(PanelModel m, ForecastView f, DashboardState s, TimeZoneInfo zone)
    {
        var now = s.Now;
        var source = L.F(f.Quality == HistoryQuality.Ok ? "SourceHabits" : "SourceLearning", Days(f.ObservedDays));
        var recharge = f.HabitualRecharge is { } hr
            ? L.F(f.ExtrapolatedFrom is null ? "HabitualRechargeNoRate" : "HabitualRecharge", Clock(Round5(hr), zone)) : null;
        m = m with { Source = EstimateSource.Habits, SourceLine = source, RechargeLine = recharge };
        switch (f.Crossing)
        {
            case CrossingStatus.AlreadyBelow:
                return m with { Headline = L.F("AlreadyBelow", Pct(f.ReservePct)) };
            case CrossingStatus.WithinHorizon when f.Central is { } c:
                var (early, late) = (f.Early, f.Late) switch
                {
                    ({ } e, { } la) => Range(e, la, now, zone),
                    ({ } e, null) => (When(e, now, zone), L.T("Later")),
                    (null, { } la) => (L.T("Earlier"), When(la, now, zone)),
                    _ => (L.T("Earlier"), L.T("Later")),
                };
                return m with
                {
                    Headline = Until(c, now, zone),
                    Detail = L.F("AboutRange", Duration(c - now), early, late),
                    Note = L.F("ToReserve", Pct(f.ReservePct)),
                };
            default:
                return m with
                {
                    Headline = L.F("Beyond", HorizonHours),
                    Detail = f.Early is { } pe ? L.F("PrudentCrosses", At(pe, now, zone)) : L.F("PrudentAbove", Pct(f.ReservePct)),
                };
        }
    }

    static string Tooltip(PanelModel m, string level, DashboardState s, TimeZoneInfo zone)
    {
        var parts = new List<string> { $"Battery Forecast · {level}" };
        if (s.Forecast is { Crossing: CrossingStatus.WithinHorizon, Central: { } c } && m.Source == EstimateSource.Habits)
            parts.Add(L.F("TipUntil", Duration(c - s.Now)));
        else if (m.Source == EstimateSource.Recent && s.Recent?.ReserveAt is { } r)
            parts.Add(L.F("TipUntil", Duration(r - s.Now)));
        if (s.AtTarget is { } p)
            parts.Add(L.F("TipAtTarget", Clock(s.Target.When, zone), Math.Round(p.P50)));
        return Tip([.. parts]);
    }

    /// <summary>NotifyIcon accetta al massimo 127 caratteri.</summary>
    static string Tip(params string[] parts)
    {
        var t = string.Join("\n", parts);
        return t.Length <= 127 ? t : t[..126] + "…";
    }

    static string Summary(ForecastView f, DateTimeOffset now)
    {
        string At(double h) => f.At(now.AddHours(h)) is { } p ? $"{Math.Round(p.P50):0}" : "—";
        return L.F("ChartSummary", Math.Round(f.Level), At(3), At(6), At(12));
    }

    // MARK: formati

    public static string Pct(double? v) => v is double d ? d.ToString("0.#", L.Culture) : "—";

    public static string Days(double d) => d.ToString(d < 10 ? "0.0" : "0", L.Culture);

    /// <summary>"3 h 20 min", "45 min": arrotondata a 5 minuti oltre l'ora, perché la stima non è più precisa.</summary>
    public static string Duration(TimeSpan t)
    {
        var minutes = Math.Max(0, (int)Math.Round(t.TotalMinutes));
        if (minutes >= 60) minutes = (int)Math.Round(minutes / 5.0) * 5;
        if (minutes < 60) return L.F("Minutes", minutes);
        return minutes % 60 == 0 ? L.F("Hours", minutes / 60) : L.F("HoursMinutes", minutes / 60, minutes % 60);
    }

    static string Hm(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";

    /// <summary>Formato dell'ora delle impostazioni regionali se sono nella stessa lingua dei testi.</summary>
    public static string Clock(DateTimeOffset t, TimeZoneInfo zone)
    {
        var region = CultureInfo.CurrentCulture;
        var culture = region.TwoLetterISOLanguageName == L.Culture.TwoLetterISOLanguageName ? region : L.Culture;
        return TimeZoneInfo.ConvertTime(t, zone).ToString("t", culture);
    }

    /// <summary>"Fino alle 14:25" oggi, "Fino a domani alle 14:25" dopo.</summary>
    public static string Until(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo zone)
    {
        t = Round5(t);
        return TimeZoneInfo.ConvertTime(t, zone).Date == TimeZoneInfo.ConvertTime(now, zone).Date
            ? L.F("Until", Clock(t, zone)) : L.F("UntilDay", When(t, now, zone));
    }

    /// <summary>Gli orari stimati si arrotondano a 5 minuti: una precisione al minuto sarebbe finta.</summary>
    static DateTimeOffset Round5(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(t.ToUnixTimeSeconds() / 300.0) * 300).ToOffset(t.Offset);

    /// <summary>
    /// Estremi della fascia: "12:50"–"13:45" oggi; "domani 00:15"–"08:45" se cadono nello stesso giorno,
    /// senza ripetere il giorno; altrimenti ciascuno con il proprio giorno.
    /// </summary>
    static (string, string) Range(DateTimeOffset early, DateTimeOffset late, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTimeOffset e = Round5(early), l = Round5(late);
        var day = TimeZoneInfo.ConvertTime(e, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (day != TimeZoneInfo.ConvertTime(l, zone).Date) return (When(early, now, zone), When(late, now, zone));
        if (day == today) return (Clock(e, zone), Clock(l, zone));
        var word = day == today.AddDays(1) ? L.T("TomorrowWord") : L.Culture.DateTimeFormat.GetDayName(day.DayOfWeek);
        return ($"{word} {Clock(e, zone)}", Clock(l, zone));
    }

    /// <summary>"alle 14:25", "domani alle 14:25", "lunedì alle 14:25".</summary>
    public static string At(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo zone) =>
        SameDay(Round5(t), now, zone) ? L.F("AtToday", Clock(Round5(t), zone)) : When(t, now, zone);

    /// <summary>"fino alle 14:25", "fino a domani alle 14:25".</summary>
    public static string UntilPhrase(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo zone) =>
        SameDay(Round5(t), now, zone) ? L.F("UntilToday", Clock(Round5(t), zone)) : L.F("UntilOther", When(t, now, zone));

    static bool SameDay(DateTimeOffset a, DateTimeOffset b, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(a, zone).Date == TimeZoneInfo.ConvertTime(b, zone).Date;

    /// <summary>Orario stimato: ora, "domani alle …" o giorno della settimana, rispetto a <paramref name="now"/>.</summary>
    public static string When(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo zone)
    {
        t = Round5(t);
        var a = TimeZoneInfo.ConvertTime(t, zone).Date;
        var b = TimeZoneInfo.ConvertTime(now, zone).Date;
        var clock = Clock(t, zone);
        if (a == b) return clock;
        if (a == b.AddDays(1)) return L.F("Tomorrow", clock);
        return L.F("DayAt", L.Culture.DateTimeFormat.GetDayName(a.DayOfWeek), clock);
    }
}
