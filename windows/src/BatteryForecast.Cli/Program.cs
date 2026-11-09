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

    case "replay":
    {
        // Rifà le previsioni di scarica del passato con il motore attuale, ogni --every minuti a batteria, e le salva
        // al posto di quelle esistenti: poi `backtest` sullo stesso file misura motore + correzione + combinazione
        // come le avrebbe viste l'app. Solo su una COPIA del database: cancella le previsioni salvate.
        var db = Opt("--db") ?? throw new ArgumentException("--db FILE");
        if (Path.GetFullPath(db).Contains(@"\BatteryForecast\data\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("usare una copia del database, non quello dell'app");
        var every = TimeSpan.FromMinutes(double.Parse(Opt("--every") ?? "10", System.Globalization.CultureInfo.InvariantCulture));
        using var store = new HistoryStore(db);
        using (var wipe = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            wipe.Open();
            using var c = wipe.CreateCommand();
            c.CommandText = "DELETE FROM forecast_snapshots; DELETE FROM forecast_runs;";
            c.ExecuteNonQuery();
        }
        var samples = store.Samples(DateTimeOffset.UnixEpoch).Where(s => s.Source == "windows" && s.LevelPct is not null).OrderBy(s => s.TimestampUtc).ToList();
        var launch = EngineLocator.Find(AppContext.BaseDirectory) ?? throw new InvalidOperationException("motore non trovato");
        await using var worker = new ForecastWorker(launch, new WorkerOptions { RequestTimeout = TimeSpan.FromSeconds(120) });
        DateTimeOffset last = DateTimeOffset.MinValue; int count = 0, drained = 0;
        foreach (var now in samples)
        {
            if (now.OnAc != false || now.LevelPct <= 11 || now.TimestampUtc - last < every) continue;
            last = now.TimestampUtc;
            var origin = now.TimestampUtc;
            var r = await worker.ForecastAsync(new ForecastRequest(origin, now.LevelPct!.Value, false, 10,
                store.ModelSamples(origin.AddDays(-110), origin), null,
                Discontinuities: store.ModelDiscontinuities(origin.AddDays(-110), origin),
                Drain: args.Contains("--no-drain") ? null : store.ModelDrain(origin.AddDays(-30), origin)));
            r = r with { Conditions = ForecastConditions.Capture(now, null, false) };
            store.RecordForecast(origin, r.Source, now.LevelPct!.Value, 10, r.Crossing.Status, null, null, null, r.Quality.State,
                r.ModelId, r.ModelRevision, r.ElapsedMs, snapshot: r, issuedAt: origin);
            count++; if (r.FromDrain) drained++;
            if (count % 50 == 0) Console.WriteLine($"{count} previsioni ({drained} dal consumo) · {origin.ToLocalTime():dd/MM HH:mm}");
        }
        Console.WriteLine($"{count} previsioni rifatte, {drained} dal consumo previsto");
        await worker.StopAsync();
        return 0;
    }

    case "calibration":
    {
        // "Impara dagli errori" su uno storico esistente (usare una COPIA del database dell'app): per l'ultima
        // previsione salvata di ogni scenario, quanti confronti trova ogni livello di somiglianza.
        var db = Opt("--db") ?? throw new ArgumentException("--db FILE");
        var apps = !args.Contains("--no-apps");
        using var store = new HistoryStore(db);
        var all = store.ForecastSnapshots(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1));
        foreach (var (source, charge) in new[] { ("timestral", false), ("timestral:charge", true) })
        {
            if (all.Where(s => s.Source == source && s.Response.Conditions is not null).MaxBy(s => s.IssuedAt) is not { } latest)
            { Console.WriteLine($"{source}: nessuna previsione salvata con condizioni"); continue; }
            var origin = DateTimeOffset.FromUnixTimeMilliseconds((long)(latest.Response.ContextEndUtc * 1000));
            var since = origin.AddDays(-30);
            var report = LocalCalibration.Learn(latest.Response, store.CalibrationSnapshots(since, origin, charge), store.Samples(since, origin),
                store.ModelDiscontinuities(since, origin), store.CalibrationContext(since, origin, apps), origin);
            Console.WriteLine($"{source} @ {latest.IssuedAt.ToLocalTime():dd/MM HH:mm}: pronta={report.Ready} ambito={report.Scope} " +
                $"confronti={report.Windows}/{LocalCalibration.MinimumWindows} sessioni={report.Sessions}/{LocalCalibration.MinimumSessions} fattore={report.Factor:0.000}");
        }
        return 0;
    }

    case "blendtrace":
    {
        var db = Opt("--db") ?? throw new ArgumentException("--db FILE");
        using var store = new HistoryStore(db);
        var trace = new List<(DateTimeOffset Origin, double Actual, double Power, double Timestral)>();
        var now = DateTimeOffset.UtcNow;
        var info = EstimateBlend.Learn(store.DischargeSnapshots(now.AddDays(-30), now), store.Samples(now.AddDays(-30), now), now, trace);
        foreach (var t in trace) Console.WriteLine($"{t.Origin.ToLocalTime():dd/MM HH:mm} reale {t.Actual,4:0} · potenza {t.Power - t.Actual,+5:+0;-0} · timestral {t.Timestral - t.Actual,+5:+0;-0}");
        Console.WriteLine($"peso {info.PowerWeight:0.00}, errori {info.PowerError:0} / {info.TimestralError:0}");
        return 0;
    }

    case "backtest":
    {
        // Prova sul passato (usare una COPIA del database): per ogni previsione di scarica salvata, l'apprendimento
        // ricalcolato con i soli dati precedenti, poi l'errore sull'arrivo reale alla riserva, originale e corretto.
        var db = Opt("--db") ?? throw new ArgumentException("--db FILE");
        using var store = new HistoryStore(db);
        var samples = store.Samples(DateTimeOffset.UnixEpoch).Where(s => s.Source == "windows" && s.LevelPct is not null).OrderBy(s => s.TimestampUtc).ToList();
        var snaps = store.DischargeSnapshots(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1))
            .Where(s => s.Source == "timestral" && s.Response.TimeBasis == "active_use" && s.Response.Conditions is { OnAc: false })
            .OrderBy(s => s.IssuedAt).ToList();
        var rawErr = new List<double>(); var calErr = new List<double>(); var ready = 0;
        var windowsErr = new List<double>(); var powerErr = new List<double>(); var blendErr = new List<double>();
        var levelErr = new[] { 60, 120 }.ToDictionary(m => m, _ => (raw: new List<double>(), cal: new List<double>()));
        using var dump = Opt("--dump") is { } dumpPath ? new StreamWriter(dumpPath) : null;   // CSV per confronti esterni
        dump?.WriteLine("origin,level,reserve,arrival,combined,power,raw,corrected");
        foreach (var snap in snaps)
        {
            var raw = LocalCalibration.Apply(snap.Response, new CalibrationInfo { Enabled = false });
            var origin = DateTimeOffset.FromUnixTimeMilliseconds((long)(raw.ContextEndUtc * 1000));
            // arrivo reale alla riserva: a batteria, senza buchi oltre 3 minuti
            int i = samples.FindIndex(s => s.TimestampUtc >= origin);
            if (i <= 0 || (samples[i].TimestampUtc - origin).TotalSeconds > 150) continue;
            DateTimeOffset? arrival = null;
            for (int k = i; k < samples.Count; k++)
            {
                if (samples[k].OnAc == true || k > i && samples[k].TimestampUtc - samples[k - 1].TimestampUtc > TimeSpan.FromMinutes(3)) break;
                if (samples[k].LevelPct <= raw.ReservePct) { arrival = samples[k].TimestampUtc; break; }
            }
            var since = origin.AddDays(-30);
            // as in the app: a curve from the forecast drain gets no level-band correction
            var report = raw.FromDrain ? new CalibrationInfo { Reason = LocalCalibration.DrainReason } :
                LocalCalibration.Learn(raw, store.CalibrationSnapshots(since, origin, false), store.Samples(since, origin),
                store.ModelDiscontinuities(since, origin), store.CalibrationContext(since, origin, !args.Contains("--no-apps")), origin);
            var corrected = LocalCalibration.Apply(raw, report);
            // livello a +60 e +120 minuti, finché il PC resta a batteria e senza buchi (anche senza arrivo alla riserva)
            foreach (var minutes in new[] { 60, 120 })
            {
                var target = origin.AddMinutes(minutes);
                int k = i;
                while (k + 1 < samples.Count && samples[k + 1].TimestampUtc <= target && samples[k + 1].OnAc != true &&
                       samples[k + 1].TimestampUtc - samples[k].TimestampUtc <= TimeSpan.FromMinutes(3)) k++;
                if ((target - samples[k].TimestampUtc).TotalSeconds > 150 || samples[k].LevelPct is not { } real || real <= raw.ReservePct) continue;
                double? LevelAt(ForecastResponse r) => r.Curve.Where(p => p.T >= target.ToUnixTimeSeconds()).Select(p => (double?)p.P50).FirstOrDefault();
                if (LevelAt(raw) is { } lr && LevelAt(corrected) is { } lc) { levelErr[minutes].raw.Add(lr - real); levelErr[minutes].cal.Add(lc - real); }
            }
            if (arrival is null) continue;
            if (report.Ready) ready++;
            var blendInfo = EstimateBlend.Learn(store.DischargeSnapshots(since, origin), store.Samples(since, origin), origin, drain: raw.FromDrain);
            var recent = RecentRate.Estimate(samples.Where(s => s.TimestampUtc <= origin && origin - s.TimestampUtc <= TimeSpan.FromMinutes(70)).ToList(), raw.ReservePct);
            var blended = EstimateBlend.Apply(corrected, recent ?? EstimateBlend.FromWindows(samples[i], raw.ReservePct) ??
                EstimateBlend.FromHistory(samples.Where(s => s.TimestampUtc < origin && s.TimestampUtc >= origin.AddDays(-7)).ToList(), samples[i], raw.ReservePct), blendInfo);
            double? Err(ForecastResponse r) => r.Crossing.CentralUtc is { } c ? (c - arrival.Value.ToUnixTimeMilliseconds() / 1000.0) / 60 : null;
            double actualMinutes = (arrival.Value - origin).TotalMinutes;
            if (samples[i].OsRemainingSeconds is int os && os > 0 && samples[i].LevelPct is double lvl && lvl > 0)
                windowsErr.Add(os / 60.0 * (lvl - raw.ReservePct) / lvl - actualMinutes);
            if (recent?.ReserveAt is { } ra) powerErr.Add((ra - origin).TotalMinutes - actualMinutes);
            if (Err(blended) is { } m) blendErr.Add(m);
            dump?.WriteLine(FormattableString.Invariant($"{origin.ToUnixTimeSeconds()},{samples[i].LevelPct},{raw.ReservePct},{arrival.Value.ToUnixTimeSeconds()},{Err(blended)},{(recent?.ReserveAt is { } rr ? (rr - origin).TotalMinutes - actualMinutes : null)},{Err(raw)},{Err(corrected)}"));
            if (Err(raw) is { } a && Err(corrected) is { } b)
            {
                rawErr.Add(a); calErr.Add(b);
                var bands = $" · peso potenza {blendInfo.PowerWeight:0.00} ({blendInfo.Evaluations} arrivi passati, errori {blendInfo.PowerError:0} / {blendInfo.TimestralError:0} min) · per durata {string.Join("/", blendInfo.StretchWeights?.Select(w => w?.ToString("0.00") ?? "-") ?? [])} · usato {blended.Blend?.PowerWeight:0.00} su {recent?.Window.TotalMinutes:0} min · combinato {Err(blended):+0;-0}";
                Console.WriteLine($"{snap.IssuedAt.ToLocalTime():dd/MM HH:mm} reale tra {(arrival.Value - origin).TotalMinutes,4:0} min · originale {a,+5:+0;-0} · corretta {b,+5:+0;-0} min · {(report.Ready ? $"{report.Scope} {report.Factor:0.00}" : $"in raccolta {report.Windows}")}{bands}");
            }
        }
        static string Summary(List<double> e) => e.Count == 0 ? "—" :
            $"n={e.Count}, errore medio assoluto {e.Average(Math.Abs):0} min, mediana {e.Order().ElementAt(e.Count / 2):+0;-0} min, entro 15 min {e.Count(x => Math.Abs(x) <= 15) * 100 / e.Count}%";
        Console.WriteLine($"\n{rawErr.Count} previsioni con arrivo reale osservato, correzione pronta in {ready}");
        Console.WriteLine($"Windows:                     {Summary(windowsErr)}");
        Console.WriteLine($"Timestral originale:         {Summary(rawErr)}");
        Console.WriteLine($"Timestral corretto:          {Summary(calErr)}");
        Console.WriteLine($"potenza misurata:            {Summary(powerErr)}");
        Console.WriteLine($"combinato (nuovo titolo):    {Summary(blendErr)}");
        foreach (var (minutes, e) in levelErr)
            if (e.raw.Count > 0)
                Console.WriteLine($"livello a +{minutes} min ({e.raw.Count} previsioni): errore medio assoluto originale {e.raw.Average(Math.Abs):0.0} punti " +
                    $"(distorsione {e.raw.Average():+0.0;-0.0}), corretta {e.cal.Average(Math.Abs):0.0} (distorsione {e.cal.Average():+0.0;-0.0})");
        return 0;
    }

    default:
        Console.Error.WriteLine("comandi: battery | sensors | forecast [--db FILE] | record --db FILE [--minutes N] | calibration --db COPIA [--no-apps] | backtest --db COPIA [--dump FILE.csv] | replay --db COPIA [--every MIN] [--no-drain]");
        return 1;
}
