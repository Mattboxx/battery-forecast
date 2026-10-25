namespace BatteryForecast.Core.Forecasting;

/// <summary>
/// Dove sta il motore. Nella release: &lt;app&gt;\engine\battery_forecast.exe e &lt;app&gt;\engine\model.
/// In sviluppo: il venv di forecast\ nel repository. BATTERY_FORECAST_MODEL sceglie un'altra cartella di pesi.
/// </summary>
public static class EngineLocator
{
    public static WorkerLaunch? Find(string appDir, int threads = 2)
    {
        var modelOverride = Environment.GetEnvironmentVariable("BATTERY_FORECAST_MODEL");
        var packaged = Path.Combine(appDir, "engine", "battery_forecast.exe");
        if (File.Exists(packaged))
        {
            var model = modelOverride ?? Path.Combine(appDir, "engine", "model");
            return new WorkerLaunch(packaged, ["--model", model, "--threads", $"{threads}"], Path.GetDirectoryName(packaged)!);
        }
        for (var d = new DirectoryInfo(appDir); d is not null; d = d.Parent)
        {
            var python = Path.Combine(d.FullName, "forecast", ".venv", "Scripts", "python.exe");
            if (!File.Exists(python)) continue;
            var forecast = Path.Combine(d.FullName, "forecast");
            var model = modelOverride ?? Path.Combine(forecast, "models", "Timestral-28M");
            return new WorkerLaunch(python, ["-m", "battery_forecast", "--model", model, "--threads", $"{threads}"],
                forecast, new Dictionary<string, string> { ["PYTHONPATH"] = Path.Combine(forecast, "src") });
        }
        return null;
    }
}
