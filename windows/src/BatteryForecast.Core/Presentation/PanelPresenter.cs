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
    /// <summary>Collegato ma con la carica ferma sotto il pieno: di solito un limite di carica del produttore.</summary>
    public string? HeldLine { get; init; }
    public string? DemoLine { get; init; }
    public string SectionTitle { get; init; } = "";
    public string? UpdatedAt { get; init; }
    public bool Busy { get; init; }
    public bool CanRecalculate { get; init; }
    public string Headline { get; init; } = "";
    public string? Detail { get; init; }
    public string? Note { get; init; }
    public EstimateSource Source { get; init; }
    public string? SourceLine { get; init; }
    public string? RechargeLine { get; init; }
    /// <summary>Il segno dell'obiettivo serale sul grafico (non in carica).</summary>
    public bool ShowEveningMarker { get; init; } = true;
    public string? RecentLine { get; init; }
    public string? WindowsLine { get; init; }
    public bool HasComparisons => !string.IsNullOrWhiteSpace(RecentLine) || !string.IsNullOrWhiteSpace(WindowsLine);
    public string ComparisonHelp => L.T(Chart is not null ? "OtherEstimatesChartHelp"
        : Source==EstimateSource.Recent ? "OtherEstimatesRecentHelp" : "OtherEstimatesPendingHelp");
    public bool Stale { get; init; }
    public bool CanRetry { get; init; }
    public string? TargetLabel { get; init; }
    public string? TargetValue { get; init; }
    public string? TargetRange { get; init; }
    public ForecastView? Chart { get; init; }
    public string? ChartSummary { get; init; }
    public string? SavingTitle { get; init; }
    public string? SavingDetail { get; init; }
    public string? SensorLine { get; init; }
    public string? ContextLine { get; init; }
    public string? ExtraTargets { get; init; }
    public string Tooltip { get; init; } = "Battery Forecast";
}

public static class PanelPresenter
{
    public static PanelModel Present(DashboardState s, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var b = s.Battery;
        var level = b.LevelPct is double l ? $"{l:0}%" : "—";
        var m = new PanelModel
        {
            Level = level,
            Busy = s.Updating,
            CanRecalculate = !s.Updating && s.Battery.LevelPct is not null &&
                s.Phase is (PowerPhase.Discharging or PowerPhase.Charging or PowerPhase.PluggedIn) &&
                s.EngineError is not ("not_found" or "model_unavailable"),
            ShowEveningMarker = s.EveningTargetEnabled,
            PowerLine = L.F("PowerSource", L.T(b.OnAc == true ? "SourceAc" : "SourceBattery")),
            SaverLine = b.BatterySaver == true ? L.T("SaverOn") : null,
            DemoLine = s.Demo ? L.T(s.Battery.OnAc == true ? "DemoCharging" : "Demo") : null,
            SectionTitle = L.T("ExpectedRuntime"),
            SensorLine = SensorLine(s),
            ContextLine = s.Forecast?.ContextFeatures.Count > 0 ? L.F("ContextUsed", string.Join(", ",s.Forecast.ContextFeatures.Select(k=>L.T("Sensor_"+k)))) : null,
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
                if (s.Phase == PowerPhase.PluggedIn)
                    return Held(m, s, level, full, zone);
                if (s.Phase == PowerPhase.Charging && s.ChargeForecast is { Quality: not HistoryQuality.Insufficient } cf)
                    return ChargeFromModel(m, cf, s, level, zone);
                if (s.Phase == PowerPhase.Charging && s.Charging is { } charge)
                {
                    var result = m with { SectionTitle = L.T("ChargeSection"), Note = L.T("ChargeUncertain") };
                    return charge.Kind switch
                    {
                        ChargeEstimateKind.AlreadyReached => result with { Headline = L.F("ChargeReached", charge.TargetPct), Tooltip = $"Battery Forecast · {level} · " + L.F("ChargeReached", charge.TargetPct) },
                        ChargeEstimateKind.RecentRate or ChargeEstimateKind.PastSessions when charge.TargetAt is { } chargeAt => result with
                        {
                            Headline = L.F("ChargeUntil", charge.TargetPct, When(Round5(chargeAt), s.Now, zone)),
                            Detail = L.F("About", Duration(chargeAt - s.Now)),
                            SourceLine = charge.Kind == ChargeEstimateKind.RecentRate ? L.T("ChargeRecentSource") : L.F("ChargeHistorySource", charge.Sessions),
                            Tooltip = $"Battery Forecast · {level} · " + L.F("ChargeUntil", charge.TargetPct, When(Round5(chargeAt), s.Now, zone)),
                        },
                        _ => result with { Headline = L.T("ChargeCollecting"), Detail = L.F("ChargeCollectingHelp", charge.TargetPct), Tooltip = $"Battery Forecast · {level} · " + L.T("ChargeCollecting") },
                    };
                }
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

        if (f is not null && habitsUsable && s.EveningTargetEnabled)
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
        }
        if (f is not null && habitsUsable)
            m = m with { Chart = f, ChartSummary = Summary(f, now) };

        if (s.EveningTargetEnabled && s.Saving != SavingSuggestion.None && s.SavingAt is { } at)
            m = m with
            {
                SavingTitle = s.Saving == SavingSuggestion.Likely ? L.F("SavingLikelyTitle", Clock(s.Target.When, zone)) : L.T("SavingPossibleTitle"),
                SavingDetail = s.Saving == SavingSuggestion.Likely ? L.F("SavingLikely", UntilPhrase(at, now, zone)) : L.F("SavingPossible", At(at, now, zone)),
            };

        if (s.EveningTargetEnabled && f is not null && habitsUsable && s.AdditionalTargets.Count > 0)
        {
            m = m with { ExtraTargets = string.Join(Environment.NewLine, s.AdditionalTargets.Select(t =>
            {
                var label = L.F(t.Tomorrow ? "AtTargetTomorrow" : "AtTarget", Clock(t.When, zone));
                var point = f.At(t.When);
                return label + ": " + (point is { } p ? p.P50 <= f.ReservePct ? L.F("TargetBelowReserve", Pct(f.ReservePct)) : $"{p.P50:0}%" : L.T("TargetBeyond"));
            })) };
        }
        return m with { Tooltip = Tooltip(m, level, s, zone) };
    }

    static string? SensorLine(DashboardState state)
    {
        if(state.SystemContext is not { } context) return null;
        var pieces=new List<string>();
        if(context.DischargeW is double discharge)pieces.Add(L.F("SensorDischarge",discharge));
        if(context.ChargeW is double charge)pieces.Add(L.F("SensorCharge",charge));
        if(context.CpuPct is double cpu)pieces.Add(L.F("SensorCpu",cpu));
        if(context.CpuMHz is double mhz)pieces.Add($"{mhz:0} MHz");
        if(context.MemoryPct is double memory)pieces.Add(L.F("SensorMemory",memory));
        if(context.CpuTemperatureC is double temp)pieces.Add(L.F("SensorCpuTemp",temp));
        if(context.BatteryTemperatureC is double battery)pieces.Add(L.F("SensorBatteryTemp",battery));
        if(context.ThermalZoneC is double zone)pieces.Add(L.F("SensorZoneTemp",zone));
        if(context.BrightnessPct is double brightness)pieces.Add(L.F("SensorBrightness",brightness));
        return pieces.Count>0?string.Join(" · ",pieces):null;
    }

    /// <summary>
    /// In carica con abbastanza storico: la previsione Timestral "resto collegato" come stima principale,
    /// il ritmo di carica recente come confronto (stessa struttura della scarica).
    /// </summary>
    static PanelModel ChargeFromModel(PanelModel m, ForecastView cf, DashboardState s, string level, TimeZoneInfo zone)
    {
        var now = s.Now;
        var target = (int)Math.Round(cf.ReservePct);
        var unplug = cf.HabitualUnplug is { } hu
            ? L.F(cf.ExtrapolatedFrom is null ? "HabitualUnplugNoRate" : "HabitualUnplug", Clock(Round5(hu), zone)) : null;
        var recent = s.Charging is { Kind: ChargeEstimateKind.RecentRate or ChargeEstimateKind.PastSessions, TargetAt: { } ra }
            ? L.F("ChargeRecentLine", Duration(ra - now), UntilPhrase(ra, now, zone)) : null;
        m = m with
        {
            SectionTitle = L.T("ChargeSection"),
            UpdatedAt = L.F("UpdatedAt", Clock(cf.Generated, zone)),
            Busy = s.Updating,
            Stale = !s.ChargeForecastFresh,
            Source = EstimateSource.Habits,
            SourceLine = ModelSource(cf, charging: true),
            RechargeLine = unplug,
            RecentLine = recent,
            Chart = cf,
            ChartSummary = Summary(cf, now),
            ShowEveningMarker = false,
        };
        switch (cf.Crossing)
        {
            case CrossingStatus.AlreadyReached:
                return m with { Headline = L.F("ChargeReached", target), Tooltip = $"Battery Forecast · {level} · " + L.F("ChargeReached", target) };
            case CrossingStatus.WithinHorizon when cf.Central is { } c:
                var (early, late) = (cf.Early, cf.Late) switch
                {
                    ({ } e, { } la) => Range(e, la, now, zone),
                    ({ } e, null) => (When(e, now, zone), L.T("Later")),
                    (null, { } la) => (L.T("Earlier"), When(la, now, zone)),
                    _ => (L.T("Earlier"), L.T("Later")),
                };
                var head = L.F("ChargeUntil", target, When(Round5(c), now, zone));
                return m with
                {
                    Headline = head,
                    Detail = L.F("AboutRange", Duration(c - now), early, late),
                    Note = L.F("ChargeToTarget", target),
                    Tooltip = Tip($"Battery Forecast · {level}", head),
                };
            default:
                return m with { Headline = L.F("ChargeBeyond", target, Duration(cf.HorizonEnd-now)), Note = L.T("ChargeUncertain"),
                    Tooltip = Tip($"Battery Forecast · {level}", L.F("ChargeBeyond", target, Duration(cf.HorizonEnd-now))) };
        }
    }

    /// <summary>
    /// Collegato con la carica ferma: la batteria non sale né scende, quindi si mostra quanto durerebbe staccando
    /// adesso (scenario di scarica di Timestral dal livello attuale), con lo stesso grafico della scarica.
    /// </summary>
    static PanelModel Held(PanelModel m, DashboardState s, string level, bool full, TimeZoneInfo zone)
    {
        var heldLine = full || s.Battery.LevelPct is not double l || l >= 97 ? null : L.F("ChargeHeld", level);
        m = m with { SectionTitle = L.T("IfUnplugNow"), HeldLine = heldLine, Busy = s.Updating, ShowEveningMarker = false };
        if (s.HeldForecast is { Quality: not HistoryQuality.Insufficient } hf)
        {
            m = HabitHeadline(m with
            {
                UpdatedAt = L.F("UpdatedAt", Clock(hf.Generated, zone)),
                Stale = !s.HeldForecastFresh,
                Chart = hf,
                ChartSummary = Summary(hf, s.Now),
            }, hf, s, zone);
            var tip = hf is { Crossing: CrossingStatus.WithinHorizon, Central: { } c } ? L.F("TipIfUnplugged", Duration(c - s.Now)) : L.T("TipPlugged");
            return m with { Tooltip = Tip($"Battery Forecast · {level}", tip) };
        }
        var head = full ? L.T("FullyCharged") : L.T("PluggedIn");
        return m with
        {
            Headline = head,
            Detail = s.Updating ? L.T("HeldCalculating") : s.HeldForecast is not null ? L.T("HeldCollecting") : null,
            Note = s.Updating || s.HeldForecast is not null ? null : L.T("ResumesOnUnplug"),
            Tooltip = Tip($"Battery Forecast · {level}", L.T("TipPlugged")),
        };
    }

    static PanelModel HabitHeadline(PanelModel m, ForecastView f, DashboardState s, TimeZoneInfo zone)
    {
        var now = s.Now;
        var source = ModelSource(f, charging: false);
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
                    Headline = L.F("Beyond", Duration(f.HorizonEnd-now)),
                    Detail = f.Early is { } pe ? L.F("PrudentCrosses", At(pe, now, zone)) : L.F("PrudentAbove", Pct(f.ReservePct)),
                };
        }
    }

    static string ModelSource(ForecastView forecast, bool charging)
    {
        if(forecast.Calibration is { Enabled:true,Ready:true } calibration && Math.Abs(calibration.Factor-1)>.001)
            return L.T("PersonalForecastSource");
        var key=(charging,forecast.TimeBasis=="active_use",forecast.Quality==HistoryQuality.Learning) switch
        {
            (true,true,true)=>"ChargeActiveUseLearningSource", (true,true,false)=>"ChargeActiveUseSource",
            (false,true,true)=>"ActiveUseLearningSource", (false,true,false)=>"ActiveUseSource",
            (true,false,true)=>"ChargeLearningSource", (true,false,false)=>"ChargeHabitsSource",
            (false,false,true)=>"SourceLearning", _=>"SourceHabits",
        };
        return L.F(key,Days(forecast.ObservedDays));
    }

    static string Tooltip(PanelModel m, string level, DashboardState s, TimeZoneInfo zone)
    {
        var parts = new List<string> { $"Battery Forecast · {level}" };
        if (s.Forecast is { Crossing: CrossingStatus.WithinHorizon, Central: { } c } && m.Source == EstimateSource.Habits)
            parts.Add(L.F("TipUntil", Duration(c - s.Now)));
        else if (m.Source == EstimateSource.Recent && s.Recent?.ReserveAt is { } r)
            parts.Add(L.F("TipUntil", Duration(r - s.Now)));
        if (s.EveningTargetEnabled && s.AtTarget is { } p)
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
