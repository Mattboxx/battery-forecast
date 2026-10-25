using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BatteryForecast.Core.Forecasting;

/// <summary>Protocollo JSON Lines con il motore Python (forecast/src/battery_forecast/protocol.py).</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int StepSeconds = 300;
    public const int HorizonSteps = 288;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static string EncodeRequest(long id, ForecastRequest r)
    {
        var o = new JsonObject
        {
            ["type"] = "forecast",
            ["protocol_version"] = Version,
            ["id"] = id,
            ["now_utc"] = r.Now.ToUnixTimeMilliseconds() / 1000.0,
            ["level"] = r.Level,
            ["ac"] = r.OnAc is bool b ? (b ? 1 : 0) : null,
            ["reserve_pct"] = r.ReservePct,
            ["horizon_steps"] = HorizonSteps,
            ["step_seconds"] = StepSeconds,
            ["scenario"] = "no_charge",
        };
        var samples = new JsonArray();
        foreach (var s in r.History)
            samples.Add(new JsonArray(s.Ts, s.Level is double l ? Math.Round(l, 2) : null, s.Ac));
        o["samples"] = samples;
        if(r.PastCovariates is { Count: >0 })
        {
            var context=new JsonObject();
            foreach(var (key,rows) in r.PastCovariates)
            {
                var values=new JsonArray();
                foreach(var row in rows)values.Add(new JsonArray(row.Ts,row.Value is double value && double.IsFinite(value)?Math.Round(value,3):null));
                context[key]=values;
            }
            o["past_covariates"]=context;
        }
        return o.ToJsonString();
    }

    /// <summary>Classifica una riga ricevuta. Le righe non JSON restituiscono null e vengono ignorate.</summary>
    public static WorkerMessage? Decode(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return null; }
        if (node is not JsonObject o) return null;
        var type = (string?)o["type"];
        long? id = o["id"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
        if ((int?)o["protocol_version"] != Version)
            return new WorkerError(id, "bad_version", $"worker protocol {o["protocol_version"]}", false);
        return type switch
        {
            "ready" => new WorkerReady((string?)o["model_id"] ?? "?", (string?)o["model_revision"] ?? "?", (int?)o["startup_ms"] ?? 0),
            "pong" => new WorkerPong(id),
            "error" => new WorkerError(id, (string?)o["code"] ?? "error", (string?)o["message"] ?? "", (bool?)o["retryable"] ?? false),
            "forecast" => new WorkerForecast(id, o.Deserialize<ForecastResponse>(Json)!),
            _ => null,
        };
    }
}

public sealed record ForecastRequest(DateTimeOffset Now, double Level, bool? OnAc, double ReservePct,
    IReadOnlyList<Storage.ModelSample> History, IReadOnlyDictionary<string,IReadOnlyList<Storage.ModelCovariate>>? PastCovariates = null);

public abstract record WorkerMessage(long? Id);
public sealed record WorkerReady(string ModelId, string ModelRevision, int StartupMs) : WorkerMessage((long?)null);
public sealed record WorkerPong(long? Id) : WorkerMessage(Id);
public sealed record WorkerError(long? Id, string Code, string Message, bool Retryable) : WorkerMessage(Id);
public sealed record WorkerForecast(long? Id, ForecastResponse Response) : WorkerMessage(Id);

public sealed record ForecastResponse
{
    public double GeneratedAtUtc { get; init; }
    public double ContextEndUtc { get; init; }
    public string Source { get; init; } = "";
    public string? ModelId { get; init; }
    public string? ModelRevision { get; init; }
    public string Scenario { get; init; } = "";
    public double ReservePct { get; init; }
    public double Level { get; init; }
    public QualityDto Quality { get; init; } = new();
    public CrossingDto Crossing { get; init; } = new();
    public double? HabitualRechargeUtc { get; init; }
    public double? ExtrapolatedFromUtc { get; init; }
    public double? ExtrapolationRatePctPerHour { get; init; }
    public List<CurvePointDto> Curve { get; init; } = [];
    public List<string> ContextFeatures { get; init; } = [];
    public int ElapsedMs { get; init; }
}

public sealed record QualityDto
{
    public string State { get; init; } = "insufficient";
    public double ObservedDays { get; init; }
    public double Coverage { get; init; }
    public int DischargeSessions { get; init; }
}

public sealed record CrossingDto
{
    public string Status { get; init; } = "beyond_horizon";
    public double? CentralUtc { get; init; }
    public double? EarlyUtc { get; init; }
    public double? LateUtc { get; init; }
}

public sealed record CurvePointDto(double T, double P10, double P50, double P90);
