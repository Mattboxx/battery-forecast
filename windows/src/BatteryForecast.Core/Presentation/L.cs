using System.Globalization;
using System.Resources;

namespace BatteryForecast.Core.Presentation;

/// <summary>Testi localizzati (Resources/Strings*.resx, generati da tools/strings.py).</summary>
public static class L
{
    static readonly ResourceManager Rm = new("BatteryForecast.Core.Resources.Strings", typeof(L).Assembly);

    /// <summary>Lingua dei testi; le date e i numeri seguono <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.CurrentUICulture;

    public static void Apply(LanguageChoice choice) => Culture = choice switch
    {
        LanguageChoice.Italian => CultureInfo.GetCultureInfo("it-IT"),
        LanguageChoice.English => CultureInfo.GetCultureInfo("en-US"),
        _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "it"
            ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo("en-US"),
    };

    public static string T(string key) => Rm.GetString(key, Culture) ?? key;

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);
}
