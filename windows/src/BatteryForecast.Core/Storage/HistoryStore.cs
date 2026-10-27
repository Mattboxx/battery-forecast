using System.Globalization;
using Microsoft.Data.Sqlite;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Storage;

/// <summary>Un passo a 5 minuti come lo legge il motore: livello medio e alimentatore se collegato in quel passo.</summary>
public readonly record struct ModelSample(long Ts, double? Level, int? Ac, bool Observed = true);
public readonly record struct ModelCovariate(long Ts,double? Value);

/// <summary>
/// Storico locale in SQLite. L'app è l'unico processo che scrive; il motore riceve i dati nella richiesta.
/// Orari in secondi UTC. Una connessione, protetta da un lock: le scritture sono brevi e rare (1 al minuto).
/// </summary>
public sealed partial class HistoryStore : IDisposable
{
    public const int SchemaVersion = 6;
    readonly SqliteConnection db;
    readonly Lock gate = new();

    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            db.Open();
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
            Migrate();
        }
        catch { db.Dispose(); throw; }
    }

    void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"database schema {version} is newer than this app ({SchemaVersion})");
        if (version < 1)
        {
            using var tx = db.BeginTransaction();
            Exec("""
                CREATE TABLE battery_samples (
                    ts INTEGER PRIMARY KEY,           -- secondi UTC; un campione per secondo al massimo
                    state INTEGER NOT NULL,
                    level REAL, on_ac INTEGER,
                    remaining_mwh INTEGER, full_mwh INTEGER, design_mwh INTEGER,
                    discharge_mw INTEGER, os_remaining_s INTEGER, saver INTEGER,
                    source TEXT NOT NULL, battery_epoch INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE power_events (ts INTEGER NOT NULL, kind TEXT NOT NULL);
                CREATE INDEX power_events_ts ON power_events(ts);
                CREATE TABLE forecast_runs (
                    ts INTEGER NOT NULL, source TEXT NOT NULL, level REAL NOT NULL, reserve REAL NOT NULL,
                    crossing_status TEXT NOT NULL, central INTEGER, early INTEGER, late INTEGER,
                    quality TEXT, model_id TEXT, model_revision TEXT, elapsed_ms INTEGER
                );
                CREATE INDEX forecast_runs_ts ON forecast_runs(ts);
                CREATE TABLE notification_events (ts INTEGER NOT NULL, rule TEXT NOT NULL, session_key TEXT NOT NULL);
                PRAGMA user_version = 1;
                """, tx);
            tx.Commit();
        }
        if (version < 2)
        {
            using var tx = db.BeginTransaction();
            Exec("CREATE TABLE notification_state (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); PRAGMA user_version=2;", tx);
            tx.Commit();
        }
        if (version < 3)
        {
            using var tx = db.BeginTransaction();
            Exec("""
                CREATE TABLE context_samples (ts INTEGER PRIMARY KEY,
                    cpu_pct REAL, cpu_mhz REAL, memory_pct REAL, cpu_temp_c REAL, battery_temp_c REAL,
                    thermal_zone_c REAL, brightness_pct REAL, discharge_w REAL, charge_w REAL, power_profile REAL,
                    source TEXT NOT NULL);
                PRAGMA user_version=3;
                """,tx);
            tx.Commit();
        }
        if (version < 4)
        {
            using var tx = db.BeginTransaction();
            Exec("ALTER TABLE context_samples ADD COLUMN display_state INTEGER NULL; PRAGMA user_version=4;", tx);
            tx.Commit();
        }
        if (version < 5)
        {
            using var tx = db.BeginTransaction();
            Exec("""
                CREATE TABLE forecast_snapshots (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL,
                    origin INTEGER NOT NULL, source TEXT NOT NULL, json TEXT NOT NULL);
                CREATE INDEX forecast_snapshots_ts ON forecast_snapshots(ts);
                PRAGMA user_version=5;
                """, tx);
            tx.Commit();
        }
        if(version < 6)
        {
            using var tx=db.BeginTransaction();
            Exec("""
                ALTER TABLE context_samples ADD COLUMN cpu_start REAL NULL;
                ALTER TABLE context_samples ADD COLUMN measured_at REAL NULL;
                ALTER TABLE context_samples ADD COLUMN collection_kind TEXT NOT NULL DEFAULT 'legacy';
                ALTER TABLE context_samples ADD COLUMN optional_observed_at REAL NULL;
                ALTER TABLE battery_samples ADD COLUMN measured_at REAL NULL;
                ALTER TABLE battery_samples ADD COLUMN collection_kind TEXT NOT NULL DEFAULT 'legacy';
                PRAGMA user_version=6;
                """,tx);
            tx.Commit();
        }
    }

    static readonly string[] ContextColumns = ["cpu_pct","cpu_mhz","memory_pct","cpu_temp_c","battery_temp_c","thermal_zone_c","brightness_pct","discharge_w","charge_w","power_profile","display_state"];

    public void AppendContext(SystemSample sample)
    {
        lock(gate)
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText=$"INSERT OR IGNORE INTO context_samples (ts,{string.Join(',',ContextColumns)},source,cpu_start,measured_at,collection_kind,optional_observed_at) VALUES ($ts,$v0,$v1,$v2,$v3,$v4,$v5,$v6,$v7,$v8,$v9,$v10,$source,$cpu_start,$measured,$kind,$optional)";
            cmd.Parameters.AddWithValue("$ts",sample.TimestampUtc.ToUnixTimeSeconds());
            var values=new double?[] { sample.CpuPct,sample.CpuMHz,sample.MemoryPct,sample.CpuTemperatureC,sample.BatteryTemperatureC,
                sample.ThermalZoneC,sample.BrightnessPct,sample.DischargeW,sample.ChargeW,sample.PowerProfile,sample.DisplayState is 0 or 1 or 2 ? sample.DisplayState : null };
            for(int i=0;i<values.Length;i++)cmd.Parameters.AddWithValue($"$v{i}",values[i] is double v && double.IsFinite(v)?v:DBNull.Value);
            cmd.Parameters.AddWithValue("$source",sample.Source);
            cmd.Parameters.AddWithValue("$cpu_start",sample.CpuIntervalStartUtc is { } start ? Seconds(start) : DBNull.Value);
            cmd.Parameters.AddWithValue("$measured",Seconds(sample.TimestampUtc));
            cmd.Parameters.AddWithValue("$kind",sample.CollectionKind);
            cmd.Parameters.AddWithValue("$optional",sample.OptionalObservedUtc is { } optional ? Seconds(optional) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<string,IReadOnlyList<ModelCovariate>> ModelCovariates(DateTimeOffset since, DateTimeOffset until, bool currentBatteryOnly = false, bool includeLegacyCpu = false)
    {
        lock(gate)
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText=$"SELECT COALESCE(measured_at,ts), {string.Join(',',ContextColumns)},cpu_start,optional_observed_at FROM context_samples WHERE ts >= $since AND COALESCE(measured_at,ts) <= $until AND ($current=0 OR ts >= COALESCE((SELECT MIN(ts) FROM battery_samples WHERE battery_epoch>0 AND battery_epoch=(SELECT battery_epoch FROM battery_samples WHERE ts <= $until ORDER BY ts DESC LIMIT 1)), $since)) ORDER BY ts";
            cmd.Parameters.AddWithValue("$since",since.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$until",Seconds(until));
            cmd.Parameters.AddWithValue("$current",currentBatteryOnly ? 1 : 0);
            using var reader=cmd.ExecuteReader();
            var readings=new List<(double T,double?[] Values,double? Start)>();
            while(reader.Read())
            {
                var values=Enumerable.Range(1,ContextColumns.Length).Select(i=>NullD(reader,i)).ToArray();
                double t=reader.GetDouble(0);
                // Cached optional observations carry their real acquisition time, never a fresh timestamp.
                if(NullD(reader,13) is { } at && (t-at>120 || at>t))
                    foreach(int c in new[]{3,4,5,6}) values[c]=null;
                readings.Add((t,values,NullD(reader,12)));
            }
            reader.Close();
            var bins=new TemporalBins(ContextColumns.Length,[9,10],Seconds(since),Seconds(until),AggregationBarriers(since,until));
            for(int i=0;i<readings.Count;i++)
            {
                var r=readings[i];
                bins.Point(r.T,i+1<readings.Count?readings[i+1].T:Seconds(until),r.Values,
                    intervalChannel:r.Start is not null || !includeLegacyCpu ? 0 : -1);
                if(r.Start is { } start) bins.Interval(0,start,r.T,r.Values[0]);
            }
            var rows=ContextColumns.ToDictionary(c=>c,_=>new List<ModelCovariate>());
            foreach(var (ts,values) in bins.Rows())
                for(int i=0;i<ContextColumns.Length;i++) rows[ContextColumns[i]].Add(new(ts,values[i]));
            return rows.ToDictionary(x=>x.Key,x=>(IReadOnlyList<ModelCovariate>)x.Value);
        }
    }

    public ContextHistory ContextSummary(DateTimeOffset since)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MAX(ts), " + string.Join(',', ContextColumns.Select(c => $"COUNT({c})")) +
                " FROM context_samples WHERE ts >= $since";
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new(reader.GetInt64(0), reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                ContextColumns.Select((name, i) => (name, count: reader.GetInt64(i + 2))).ToDictionary(x => x.name, x => x.count));
        }
    }

    public int ExportContextCsv(TextWriter writer)
    {
        lock(gate)
        {
            using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT ts,{string.Join(',',ContextColumns)},source,cpu_start,measured_at,collection_kind,optional_observed_at FROM context_samples ORDER BY ts";
            using var reader=cmd.ExecuteReader();
            writer.WriteLine("timestamp_utc,"+string.Join(',',ContextColumns)+",source,cpu_interval_start_unix,measured_at_unix,collection_kind,optional_observed_at_unix");
            int count=0;
            while(reader.Read())
            {
                var values=new List<string> { DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToString("O",CultureInfo.InvariantCulture) };
                for(int i=1;i<=ContextColumns.Length;i++)values.Add(reader.IsDBNull(i)?"":reader.GetDouble(i).ToString("0.###",CultureInfo.InvariantCulture));
                values.Add(reader.GetString(ContextColumns.Length+1));
                for(int i=ContextColumns.Length+2;i<reader.FieldCount;i++) values.Add(reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture)!);
                writer.WriteLine(string.Join(',',values));count++;
            }
            return count;
        }
    }

    public string? LoadNotificationState()
    {
        lock (gate) return Scalar("SELECT json FROM notification_state WHERE id=1") as string;
    }

    // State and decision are committed together, before asking Windows to display it.
    // The event records a dispatch attempt, not proof that Windows showed a banner.
    public void SaveNotificationState(string json, DateTimeOffset now, string session, string? rule)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO notification_state VALUES (1,$json)";
            cmd.Parameters.AddWithValue("$json", json);
            cmd.ExecuteNonQuery();
            if (rule is not null)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = "INSERT INTO notification_events VALUES ($ts,$rule,$session)";
                cmd.Parameters.AddWithValue("$ts", now.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$rule", rule);
                cmd.Parameters.AddWithValue("$session", session);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<DateTimeOffset> Discontinuities(DateTimeOffset since)
    {
        return [..ModelDiscontinuities(since,DateTimeOffset.MaxValue)];
    }

    public IReadOnlyList<DateTimeOffset> ModelDiscontinuities(DateTimeOffset since,DateTimeOffset until)
    {
        lock(gate) return AggregationBarriers(since,until).Select(t=>DateTimeOffset.FromUnixTimeSeconds((long)t)).ToList();
    }

    // MARK: campioni

    /// <summary>Aggiunge una lettura. Un secondo già registrato viene ignorato (nessun duplicato).</summary>
    public bool Append(BatterySample s, string collectionKind = "unspecified")
    {
        if (!s.HasBattery) return false;
        lock (gate)
        {
            var epoch = CurrentEpoch(s.DesignMWh);
            var previousEpoch = Scalar("SELECT battery_epoch FROM battery_samples ORDER BY ts DESC LIMIT 1") as long?;
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO battery_samples
                (ts, state, level, on_ac, remaining_mwh, full_mwh, design_mwh, discharge_mw, os_remaining_s, saver, source, battery_epoch, measured_at, collection_kind)
                VALUES ($ts, $state, $level, $ac, $rem, $full, $design, $dis, $os, $saver, $src, $epoch, $measured, $kind)
                """;
            cmd.Parameters.AddWithValue("$ts", s.TimestampUtc.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$state", (int)s.State);
            cmd.Parameters.AddWithValue("$level", (object?)s.LevelPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ac", s.OnAc is bool b ? (b ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("$rem", (object?)s.RemainingMWh ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$full", (object?)s.FullChargeMWh ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$design", (object?)s.DesignMWh ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dis", (object?)s.DischargePowerMW ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$os", (object?)s.OsRemainingSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$saver", s.BatterySaver is bool v ? (v ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("$src", s.Source);
            cmd.Parameters.AddWithValue("$epoch", epoch);
            cmd.Parameters.AddWithValue("$measured",Seconds(s.TimestampUtc));
            cmd.Parameters.AddWithValue("$kind",collectionKind);
            var inserted=cmd.ExecuteNonQuery()==1;
            if(inserted && previousEpoch is { } previous && previous!=epoch)
                AppendEvent(s.TimestampUtc,"battery_changed");
            return inserted;
        }
    }

    /// <summary>
    /// Epoca della batteria: cambia quando cambia la capacità di progetto (batteria sostituita).
    /// Le vecchie epoche restano nello storico ma non si mescolano nelle statistiche di salute.
    /// </summary>
    long CurrentEpoch(int? design)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT design_mwh, battery_epoch FROM battery_samples WHERE design_mwh IS NOT NULL ORDER BY ts DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return 0;
        var (lastDesign, epoch) = (r.GetInt64(0), r.GetInt64(1));
        return design is int d && d != lastDesign ? epoch + 1 : epoch;
    }

    /// <summary>Storico aggregato a 5 minuti per il motore, dal più vecchio.</summary>
    public List<ModelSample> ModelSamples(DateTimeOffset since, DateTimeOffset? until = null)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(measured_at,ts), level, on_ac
                FROM battery_samples WHERE ts >= $since AND COALESCE(measured_at,ts) <= $until
                    AND battery_epoch=(SELECT battery_epoch FROM battery_samples WHERE ts <= $until ORDER BY ts DESC LIMIT 1)
                ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$until", Seconds(until ?? DateTimeOffset.MaxValue));
            using var r = cmd.ExecuteReader();
            var readings = new List<(double T,double?[] Values)>();
            while (r.Read())
                readings.Add((r.GetDouble(0),[NullD(r,1),NullD(r,2)]));
            r.Close();
            double end=until is { } bound ? Seconds(bound) : readings.Count>0 ? readings[^1].T : Seconds(since);
            var bins=new TemporalBins(2,[1],Seconds(since),end,AggregationBarriers(since,until ?? DateTimeOffset.MaxValue));
            for(int i=0;i<readings.Count;i++)
                bins.Point(readings[i].T,i+1<readings.Count?readings[i+1].T:end,readings[i].Values);
            return bins.Rows().Select(row=>new ModelSample(row.Ts,row.Values[0],row.Values[1] is { } ac ? (int)ac : null,bins.IsObserved(row.Ts,0))).ToList();
        }
    }

    /// <summary>Campioni completi dal più vecchio, per la stima del consumo recente e lo storico.</summary>
    public List<BatterySample> Samples(DateTimeOffset since, DateTimeOffset? until = null)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT ts, state, level, on_ac, remaining_mwh, full_mwh, design_mwh, discharge_mw, os_remaining_s, saver, source
                FROM battery_samples WHERE ts >= $since AND ts <= $until ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$until", (until ?? DateTimeOffset.MaxValue).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<BatterySample>();
            while (r.Read())
                list.Add(new BatterySample(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)), (ChargeState)r.GetInt32(1),
                    NullD(r, 2), r.IsDBNull(3) ? null : r.GetInt32(3) == 1,
                    NullI(r, 4), NullI(r, 5), NullI(r, 6), NullI(r, 7), NullI(r, 8),
                    r.IsDBNull(9) ? null : r.GetInt32(9) == 1, r.GetString(10)));
            return list;
        }
    }

    /// <summary>Ultimo campione con l'alimentatore collegato: una previsione più vecchia non vale più.</summary>
    public DateTimeOffset? LastAcTimestamp()
    {
        lock (gate)
            return Scalar("SELECT MAX(ts) FROM battery_samples WHERE on_ac = 1") is long ts
                ? DateTimeOffset.FromUnixTimeSeconds(ts) : null;
    }

    static double? NullD(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    static int? NullI(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    static double Seconds(DateTimeOffset t) => t.ToUnixTimeMilliseconds()/1000.0;

    List<double> AggregationBarriers(DateTimeOffset since,DateTimeOffset until)
    {
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT DISTINCT ts FROM power_events WHERE ts >= $since AND ts <= $until AND kind IN ('suspend','resume','resume_detected','sampling_gap_start','sampling_gap','clock_changed','app_exit','app_start','ac_connected','ac_disconnected','battery_changed','context_disabled','context_enabled') ORDER BY ts";
        cmd.Parameters.AddWithValue("$since",since.ToUnixTimeSeconds());cmd.Parameters.AddWithValue("$until",Seconds(until));
        using var reader=cmd.ExecuteReader(); var result=new List<double>();
        while(reader.Read()) result.Add(reader.GetDouble(0));
        return result;
    }

    // MARK: eventi e previsioni

    public void AppendEvent(DateTimeOffset ts, string kind)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO power_events (ts, kind) VALUES ($ts, $kind)";
            cmd.Parameters.AddWithValue("$ts", ts.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.ExecuteNonQuery();
        }
    }

    public void RecordForecast(DateTimeOffset ts, string source, double level, double reserve, string status,
        DateTimeOffset? central, DateTimeOffset? early, DateTimeOffset? late, string? quality,
        string? modelId, string? modelRevision, int? elapsedMs,
        Forecasting.ForecastResponse? snapshot = null, DateTimeOffset? issuedAt = null)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO forecast_runs VALUES ($ts, $src, $level, $reserve, $status, $c, $e, $l, $q, $mid, $mrev, $ms)
                """;
            cmd.Parameters.AddWithValue("$ts", ts.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$reserve", reserve);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$c", (object?)central?.ToUnixTimeSeconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)early?.ToUnixTimeSeconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$l", (object?)late?.ToUnixTimeSeconds() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$q", (object?)quality ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mid", (object?)modelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mrev", (object?)modelRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", (object?)elapsedMs ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            if (snapshot is not null) InsertForecastSnapshot(snapshot, issuedAt ?? DateTimeOffset.UtcNow, source, tx);
            tx.Commit();
        }
    }

    // MARK: manutenzione

    /// <summary>Cancella ciò che è più vecchio di <paramref name="keepDays"/> giorni.</summary>
    public int Prune(DateTimeOffset now, int keepDays)
    {
        var cutoff = now.AddDays(-keepDays).ToUnixTimeSeconds();
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var n = 0;
            foreach (var table in new[] { "battery_samples", "power_events", "forecast_runs", "notification_events", "context_samples", "forecast_snapshots" })
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table} WHERE ts < $cutoff";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n;
        }
    }

    /// <summary>Cancella tutto lo storico (comando esplicito dell'utente).</summary>
    public void Wipe()
    {
        lock (gate)
        {
            Exec("DELETE FROM battery_samples; DELETE FROM power_events; DELETE FROM forecast_runs; DELETE FROM notification_events; DELETE FROM notification_state; DELETE FROM context_samples; DELETE FROM forecast_snapshots;");
            Exec("VACUUM;");
        }
    }

    /// <summary>Esporta i campioni in CSV (punto come separatore decimale, orari ISO 8601 UTC).</summary>
    public int ExportCsv(TextWriter w)
    {
        var rows = Samples(DateTimeOffset.FromUnixTimeSeconds(0));
        w.WriteLine("timestamp_utc,state,level_pct,on_ac,remaining_mwh,full_charge_mwh,design_mwh,discharge_mw,os_remaining_s,battery_saver,source");
        foreach (var s in rows)
            w.WriteLine(string.Join(',',
                s.TimestampUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                s.State, F(s.LevelPct), B(s.OnAc), s.RemainingMWh, s.FullChargeMWh, s.DesignMWh,
                s.DischargePowerMW, s.OsRemainingSeconds, B(s.BatterySaver), s.Source));
        return rows.Count;

        static string F(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        static string B(bool? v) => v is bool b ? (b ? "1" : "0") : "";
    }

    void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => db.Dispose();
}
