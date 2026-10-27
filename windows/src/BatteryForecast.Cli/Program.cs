// Strumento da riga di comando per sviluppo e diagnostica. Non scrive nello storico dell'app,
// salvo `record`, che usa un database indicato esplicitamente.
//
//   tilldusk battery                      lettura attuale della batteria (JSON)
//   tilldusk forecast [--db FILE]         lettura attuale + previsione Timestral e consumo recente
//   tilldusk record --db FILE [--minutes N]   registra un campione al minuto per N minuti
using System.Text.Json;
using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
var battery = new WindowsBatteryProvider();
var cmd = args.FirstOrDefault() ?? "battery";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

switch (cmd)
{
    case "battery":
        Console.WriteLine(JsonSerializer.Serialize(battery.Read(), json));
        return 0;

    case "sensors":
    {
        var context=new WindowsSystemContext(collectProcesses:()=>args.Contains("--processes"));
        SystemSample reading=context.Read(battery.Read());
        for(int i=0;i<3;i++) { await Task.Delay(1000); reading=context.Read(battery.Read()); }
        Console.WriteLine(JsonSerializer.Serialize(reading,json));
        Console.Error.WriteLine(JsonSerializer.Serialize(new { optional_sensor_errors=context.OptionalSensorErrors },json));
        return 0;
    }

    case "record":
    {
        var db = Opt("--db") ?? throw new ArgumentException("--db FILE");
        var minutes = int.Parse(Opt("--minutes") ?? "60");
        using var store = new HistoryStore(db);
        for (var i = 0; i < minutes; i++)
        {
            var s = battery.Read();
            store.Append(s);
            Console.WriteLine($"{s.TimestampUtc:HH:mm:ss} {s.LevelPct}% ac={s.OnAc} {s.DischargePowerMW} mW");
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
        return 0;
    }

    case "forecast":
    {
        var now = battery.Read();
        Console.WriteLine($"batteria: {now.LevelPct}% · alimentatore {now.OnAc} · {now.State} · {now.DischargePowerMW} mW");
        if (now.LevelPct is not double level) { Console.Error.WriteLine("nessuna percentuale disponibile"); return 2; }

        List<ModelSample> history = [];
        IReadOnlyList<DateTimeOffset> discontinuities = [];
        List<BatterySample> recent = [now];
        if (Opt("--db") is { } db)
        {
            using var store = new HistoryStore(db);
            history = store.ModelSamples(now.TimestampUtc.AddDays(-110),now.TimestampUtc);
            discontinuities=store.ModelDiscontinuities(now.TimestampUtc.AddDays(-110),now.TimestampUtc);
            recent = [.. store.Samples(now.TimestampUtc.AddHours(-1)), now];
        }

        if (RecentRate.Estimate(recent, 5) is { } r)
            Console.WriteLine($"consumo recente ({r.Method}): {r.PctPerHour:0.0}%/h · riserva alle {r.ReserveAt?.ToLocalTime():HH:mm}");
        else
            Console.WriteLine("consumo recente: dati insufficienti");

        var launch = EngineLocator.Find(AppContext.BaseDirectory) ?? throw new InvalidOperationException("motore non trovato");
        await using var worker = new ForecastWorker(launch);
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        await worker.WarmUpAsync();
        var startup = t0.Elapsed;
        t0.Restart();
        var f = await worker.ForecastAsync(new ForecastRequest(now.TimestampUtc, level, now.OnAc, 5, history,Discontinuities:discontinuities));
        var call = t0.Elapsed;
        Console.WriteLine($"motore {worker.Info?.ModelId}@{worker.Info?.ModelRevision?[..7]} · avvio {startup.TotalSeconds:0.0} s · previsione {call.TotalMilliseconds:0} ms (interno {f.ElapsedMs} ms)");
        Console.WriteLine($"qualità {f.Quality.State}: {f.Quality.ObservedDays} giorni, {f.Quality.DischargeSessions} sessioni, copertura {f.Quality.Coverage:P0}");
        string T(double? t) => t is double v ? DateTimeOffset.FromUnixTimeMilliseconds((long)(v * 1000)).ToLocalTime().ToString("ddd HH:mm") : "—";
        Console.WriteLine($"riserva 5%: {f.Crossing.Status} · prudente {T(f.Crossing.EarlyUtc)} · centrale {T(f.Crossing.CentralUtc)} · ottimista {T(f.Crossing.LateUtc)}");
        foreach (var i in new[] { 11, 35, 71, 143, 287 })
            Console.WriteLine($"  {T(f.Curve[i].T)}  {f.Curve[i].P10,5:0.0} {f.Curve[i].P50,5:0.0} {f.Curve[i].P90,5:0.0}");
        await worker.StopAsync();
        return 0;
    }

    default:
        Console.Error.WriteLine("comandi: battery | sensors | forecast [--db FILE] | record --db FILE [--minutes N]");
        return 1;
}
