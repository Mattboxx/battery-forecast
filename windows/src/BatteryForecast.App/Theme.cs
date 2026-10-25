using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using BatteryForecast.Core;
using BatteryForecast.Core.Presentation;

namespace BatteryForecast.App;

/// <summary>Tema chiaro/scuro/sistema e alto contrasto. Le viste usano solo DynamicResource.</summary>
static class Theme
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }
    static ResourceDictionary? current;

    public static void Apply(ThemeChoice choice)
    {
        var file = choice switch
        {
            ThemeChoice.System => ReadFlag("AppsUseLightTheme", true) ? "Light" : "Dark",
            _ => choice.ToString(),
        };
        IsDark = file is "Dark" or "Midnight";
#pragma warning disable WPF0001   // ThemeMode (Fluent) è ancora marcato sperimentale: dà ai controlli standard l'aspetto di Windows 11
        Application.Current.ThemeMode = SystemParameters.HighContrast ? ThemeMode.System : IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        // ThemeMode aggiunge i propri dizionari Fluent: il nostro si sostituisce per riferimento, non per posizione
        var dicts = Application.Current.Resources.MergedDictionaries;
        if (current is not null) dicts.Remove(current);
        current = SystemParameters.HighContrast ? HighContrast()
            : new ResourceDictionary { Source = new Uri($"Themes/{file}.xaml", UriKind.Relative) };
        dicts.Add(current);
    }

    /// <summary>La barra delle applicazioni ha il suo tema: l'icona va disegnata per quello, non per l'app.</summary>
    public static bool TaskbarIsDark => !ReadFlag("SystemUsesLightTheme", false);

    static bool ReadFlag(string name, bool fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return k?.GetValue(name) is int v ? v != 0 : fallback;
        }
        catch (System.Security.SecurityException) { return fallback; }
    }

    static ResourceDictionary HighContrast()
    {
        var d = new ResourceDictionary
        {
            ["WindowBg"] = SystemColors.WindowBrush,
            ["WindowBorder"] = SystemColors.WindowTextBrush,
            ["CardBg"] = SystemColors.WindowBrush,
            ["RowHover"] = SystemColors.HighlightBrush,
            ["Text"] = SystemColors.WindowTextBrush,
            ["TextSecondary"] = SystemColors.WindowTextBrush,
            ["TextTertiary"] = SystemColors.GrayTextBrush,
            ["Accent"] = SystemColors.HotTrackBrush,
            ["AccentText"] = SystemColors.HighlightTextBrush,
            ["Curve"] = SystemColors.HotTrackBrush,
            ["Grid"] = SystemColors.GrayTextBrush,
            ["Reserve"] = SystemColors.WindowTextBrush,
            ["Saving"] = SystemColors.WindowTextBrush,
            ["Demo"] = SystemColors.WindowBrush,
            ["DemoText"] = SystemColors.WindowTextBrush,
            ["Focus"] = SystemColors.HighlightBrush,
            ["Icon"] = SystemColors.WindowTextBrush,
            ["HeroBorder"] = SystemColors.WindowTextBrush,
            ["Hero"] = SystemColors.WindowBrush,
            ["BandColor"] = SystemColors.HotTrackColor,
        };
        var band = SystemColors.HotTrackBrush.Clone();
        band.Opacity = 0.3;
        d["Band"] = band;
        return d;
    }
}

/// <summary>Testo localizzato in XAML: Text="{app:T Battery}". Si legge alla creazione della finestra.</summary>
public sealed class T(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
