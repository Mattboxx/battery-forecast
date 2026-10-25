using System.Globalization;
using Microsoft.Data.Sqlite;
using BatteryForecast.Core.Battery;

namespace BatteryForecast.Core.Storage;

/// <summary>Un passo a 5 minuti come lo legge il motore: livello medio e alimentatore se collegato in quel passo.</summary>
public readonly record struct ModelSample(long Ts, double? Level, int? Ac);

/// <summary>
/// Storico locale in SQLite. L'app è l'unico processo che scrive; il motore riceve i dati nella richiesta.
/// Orari in secondi UTC. Una connessione, protetta da un lock: le scritture sono brevi e rare (1 al minuto).
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int SchemaVersion = 2;
    readonly SqliteConnection db;
    readonly Lock gate = new();

    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
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
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT ts FROM power_events WHERE ts >= $ts AND kind IN ('suspend','resume','app_exit','app_start','ac_connected') ORDER BY ts";
            cmd.Parameters.AddWithValue("$ts", since.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var result = new List<DateTimeOffset>();
            while (r.Read()) result.Add(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)));
            return result;
        }
    }

    // MARK: campioni

    /// <summary>Aggiunge una lettura. Un secondo già registrato viene ignorato (nessun duplicato).</summary>
    public bool Append(BatterySample s)
    {
        if (!s.HasBattery) return false;
        lock (gate)
        {
            var epoch = CurrentEpoch(s.DesignMWh);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO battery_samples
                (ts, state, level, on_ac, remaining_mwh, full_mwh, design_mwh, discharge_mw, os_remaining_s, saver, source, battery_epoch)
                VALUES ($ts, $state, $level, $ac, $rem, $full, $design, $dis, $os, $saver, $src, $epoch)
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
            return cmd.ExecuteNonQuery() == 1;
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
    public List<ModelSample> ModelSamples(DateTimeOffset since)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT (ts / 300) * 300 AS bin, AVG(level), MAX(on_ac)
                FROM battery_samples WHERE ts >= $since GROUP BY bin ORDER BY bin
                """;
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<ModelSample>();
            while (r.Read())
                list.Add(new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetDouble(1), r.IsDBNull(2) ? null : r.GetInt32(2)));
            return list;
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
        string? modelId, string? modelRevision, int? elapsedMs)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
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
            foreach (var table in new[] { "battery_samples", "power_events", "forecast_runs", "notification_events" })
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
            Exec("DELETE FROM battery_samples; DELETE FROM power_events; DELETE FROM forecast_runs; DELETE FROM notification_events; DELETE FROM notification_state;");
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
