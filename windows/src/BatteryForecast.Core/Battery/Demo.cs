using BatteryForecast.Core.Storage;

namespace BatteryForecast.Core.Battery;

/// <summary>
/// Modalità demo: dati sintetici, deterministici e dichiarati (per screenshot e prove della UI).
/// Parte dal livello reale della batteria, così i numeri sono riconoscibili, ma da lì la scarica è
/// simulata: anche a corrente il pannello mostra una scarica. Vive in una cartella separata e non tocca
/// mai lo storico reale. Non è un benchmark: l'andamento è inventato.
/// </summary>
public static class Demo
{
    public const double RatePerHour = 11, MinStart = 20, FallbackStart = 63;

    /// <summary>Livello di partenza: quello reale, ma non sotto il 20% (sotto non resterebbe nulla da mostrare).</summary>
    public static double StartFrom(BatterySample real) =>
        real.LevelPct is double l ? Math.Clamp(Math.Round(l), MinStart, 100) : FallbackStart;

    /// <summary>Scarica simulata a ~11%/h da <paramref name="startLevel"/>, con piccole variazioni di potenza ripetibili.</summary>
    public sealed class Provider(DateTimeOffset start, double startLevel, int fullMWh, int designMWh, TimeProvider? clock = null)
        : IBatteryProvider
    {
        readonly TimeProvider clock = clock ?? TimeProvider.System;

        public BatterySample Read()
        {
            var now = clock.GetUtcNow();
            var hours = Math.Max(0, (now - start).TotalHours);
            var level = Math.Max(1, startLevel - RatePerHour * hours);
            var mw = (int)(fullMWh * RatePerHour / 100 * (1 + 0.08 * Math.Sin(hours * 9)));
            return new BatterySample(now, ChargeState.Discharging, Math.Round(level), false,
                (int)(fullMWh * level / 100), fullMWh, designMWh, mw, (int)(level / RatePerHour * 3600), false, "demo");
        }
    }

    /// <summary>
    /// Sette giorni di abitudini (notte in carica, scarica la mattina e il pomeriggio, sera a corrente),
    /// poi gli ultimi 90 minuti a batteria fino al livello del <see cref="Provider"/>: nessun salto.
    /// </summary>
    public static void Seed(HistoryStore store, DateTimeOffset now, TimeZoneInfo zone, double startLevel, int fullMWh, int designMWh)
    {
        var level = 100.0;
        var unplugged = now.AddMinutes(-90);
        for (var t = now.AddDays(-7); t < unplugged; t = t.AddMinutes(5))
        {
            var h = TimeZoneInfo.ConvertTime(t, zone).TimeOfDay.TotalHours;
            var day = (int)(t - now.AddDays(-7)).TotalDays;
            var onBattery = (h is >= 8.5 and < 12.5) || (h >= 14 && h < 18 + day % 3 * 0.5);
            var rate = h < 12.5 ? 9.5 + day % 2 : 12;
            level = onBattery ? Math.Max(3, level - rate / 12) : Math.Min(100, level + 30.0 / 12);
            store.Append(new BatterySample(t, onBattery ? ChargeState.Discharging : ChargeState.Charging,
                Math.Round(level), !onBattery, Source: "demo"));
        }
        // ricarica fino al livello da cui parte la scarica attuale, poi scarica a ritmo costante
        var from = Math.Min(100, startLevel + RatePerHour * 1.5);
        var lastRate = (from - startLevel) / 1.5;
        store.Append(new BatterySample(unplugged.AddMinutes(-5), ChargeState.Charging, Math.Round(from), true, Source: "demo"));
        for (var t = unplugged; t < now; t = t.AddMinutes(1))
        {
            var l = from - lastRate * (t - unplugged).TotalHours;
            store.Append(new BatterySample(t, ChargeState.Discharging, Math.Round(l), false, (int)(fullMWh * l / 100),
                fullMWh, designMWh, (int)(fullMWh * Math.Max(lastRate, 1) / 100), Source: "demo"));
        }
    }
}
