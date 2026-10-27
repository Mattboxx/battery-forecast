using System.Text.Json;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Core.Storage;

public sealed partial class HistoryStore
{
    public IReadOnlyList<ForecastSnapshot> CalibrationSnapshots(DateTimeOffset since,DateTimeOffset until,bool charge)
    {
        lock(gate)
        {
            using var cmd=db.CreateCommand();
            // Thin rapid manual requests before the bounded read, rather than losing older independent sessions.
            cmd.CommandText="""
                SELECT id,ts,source,json FROM forecast_snapshots WHERE id IN (
                    SELECT MIN(id) FROM forecast_snapshots
                    WHERE ts >= $since AND ts <= $until AND source=$source AND json_valid(json)
                    GROUP BY origin/1800,json_extract(json,'$.Conditions.WorkloadKey'),json_extract(json,'$.Conditions.CpuBand')
                ) ORDER BY ts DESC,id DESC LIMIT 200
                """;
            cmd.Parameters.AddWithValue("$since",since.ToUnixTimeSeconds());cmd.Parameters.AddWithValue("$until",until.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$source",charge?"timestral:charge":"timestral");
            using var reader=cmd.ExecuteReader();var rows=new List<ForecastSnapshot>();
            while(reader.Read())
                try
                {
                    if(JsonSerializer.Deserialize<ForecastResponse>(reader.GetString(3)) is { Curve.Count:>0 } response)
                        rows.Add(new(reader.GetInt64(0),DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),reader.GetString(2),response));
                }
                catch(JsonException) { }
            return rows;
        }
    }

    public IReadOnlyList<CalibrationContext> CalibrationContext(DateTimeOffset since,DateTimeOffset until,bool includeApps)
    {
        lock(gate)
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText="""
                SELECT COALESCE(measured_at,ts),cpu_pct,display_state,brightness_pct,workload_profile,
                    CASE WHEN $apps=1 THEN processes_json ELSE NULL END
                FROM context_samples WHERE ts >= $since AND COALESCE(measured_at,ts) <= $until ORDER BY ts
                """;
            cmd.Parameters.AddWithValue("$since",since.ToUnixTimeSeconds());cmd.Parameters.AddWithValue("$until",Seconds(until));
            cmd.Parameters.AddWithValue("$apps",includeApps?1:0);
            using var reader=cmd.ExecuteReader();var rows=new List<CalibrationContext>();
            while(reader.Read())
            {
                string? key=null;
                if(includeApps && !reader.IsDBNull(4) && !reader.IsDBNull(5))
                    try { key=ForecastConditions.AppKey(reader.GetInt64(4),JsonSerializer.Deserialize<ProcessLoad[]>(reader.GetString(5))); }
                    catch(JsonException) { }
                rows.Add(new(DateTimeOffset.FromUnixTimeMilliseconds((long)(reader.GetDouble(0)*1000)),
                    ForecastConditions.Band(NullD(reader,1)),NullD(reader,2),NullD(reader,3),key));
            }
            return rows;
        }
    }
}
