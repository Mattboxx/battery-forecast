using System.Text.Json;
using BatteryForecast.Core.Forecasting;
using Microsoft.Data.Sqlite;

namespace BatteryForecast.Core.Storage;

/// <summary>An original response saved when accepted, never a retrospective recalculation.</summary>
public sealed record ForecastSnapshot(long Id, DateTimeOffset IssuedAt, string Source, ForecastResponse Response)
{
    public ForecastView View { get; } = ForecastView.From(Response);
}

public sealed partial class HistoryStore
{
    /// <summary>Keep the genuine last response retained by an older app, without reconstructing missing curves.</summary>
    public bool PreserveCachedForecast(ForecastResponse response, DateTimeOffset issuedAt)
    {
        lock (gate)
        {
            using var check = db.CreateCommand();
            check.CommandText = "SELECT 1 FROM forecast_snapshots WHERE origin=$origin AND source=$source LIMIT 1";
            check.Parameters.AddWithValue("$origin", (long)response.ContextEndUtc);
            check.Parameters.AddWithValue("$source", response.Source);
            if (check.ExecuteScalar() is not null) return false;
            using var tx = db.BeginTransaction();
            InsertForecastSnapshot(response, issuedAt, response.Source, tx);
            tx.Commit();
            return true;
        }
    }

    void InsertForecastSnapshot(ForecastResponse response, DateTimeOffset issuedAt, string source, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO forecast_snapshots(ts,origin,source,json) VALUES($ts,$origin,$source,$json)";
        cmd.Parameters.AddWithValue("$ts", issuedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$origin", (long)response.ContextEndUtc);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(response));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ForecastSnapshot> ForecastSnapshots(DateTimeOffset since, DateTimeOffset until, int limit = 100)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,ts,source,json FROM forecast_snapshots WHERE ts >= $since AND ts <= $until ORDER BY ts DESC,id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$until", until.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
            using var reader = cmd.ExecuteReader();
            var result = new List<ForecastSnapshot>();
            while (reader.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<ForecastResponse>(reader.GetString(3)) is { Curve.Count: > 0 } response)
                        result.Add(new(reader.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)), reader.GetString(2), response));
                }
                catch (JsonException) { /* A malformed row must not prevent reading the rest of the local history. */ }
            }
            return result;
        }
    }
}
