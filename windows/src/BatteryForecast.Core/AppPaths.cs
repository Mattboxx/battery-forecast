namespace BatteryForecast.Core;

/// <summary>Cartelle dati per utente: %LOCALAPPDATA%\BatteryForecast\{data,cache,logs}. BATTERY_FORECAST_HOME le sposta (test, demo).</summary>
public sealed record AppPaths(string Root)
{
    public static AppPaths Default => new(Environment.GetEnvironmentVariable("BATTERY_FORECAST_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryForecast"));

    /// <summary>
    /// Prima del cambio di nome l'app si chiamava Till Dusk for Windows e scriveva in %LOCALAPPDATA%\TillDusk.
    /// Al primo avvio con il nuovo nome storico e preferenze si spostano, se la nuova cartella non esiste ancora.
    /// </summary>
    public static bool MigrateLegacy(AppPaths target)
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TillDusk");
        if (!Directory.Exists(legacy) || Directory.Exists(target.Root)) return false;
        try
        {
            Directory.Move(legacy, target.Root);
            return true;
        }
        catch (IOException) { return false; }         // file aperti da una vecchia istanza: si riprova al prossimo avvio
        catch (UnauthorizedAccessException) { return false; }
    }

    public string Data => Path.Combine(Root, "data");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string History => Path.Combine(Data, "history.db");
    public string Settings => Path.Combine(Data, "settings.json");
    public string LastForecast => Path.Combine(Cache, "last_forecast.json");

    public AppPaths Ensure()
    {
        foreach (var d in new[] { Data, Cache, Logs }) Directory.CreateDirectory(d);
        return this;
    }

    /// <summary>Scrittura atomica: file temporaneo nella stessa cartella, poi sostituzione.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
