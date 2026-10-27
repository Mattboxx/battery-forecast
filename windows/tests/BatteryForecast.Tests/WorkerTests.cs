using System.Text.Json.Nodes;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Tests;

public class ProtocolTests
{
    [Fact]
    public void Request_has_version_unix_seconds_and_compact_samples()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var line = Protocol.EncodeRequest(3, new ForecastRequest(now, 55.5, false, 5, [new(1_799_999_700, 56.123, 0), new(1_799_999_400, null, null,Observed:false)]));
        var o = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(1, (int)o["protocol_version"]!);
        Assert.Equal(1_800_000_000.0, (double)o["now_utc"]!);
        Assert.Equal(0, (int)o["ac"]!);
        Assert.Equal("[1799999700,56.12,0,true]", o["samples"]![0]!.ToJsonString());
        Assert.Equal("[1799999400,null,null,false]", o["samples"]![1]!.ToJsonString());
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void BoundaryExtensionIsOrderedDistinctAndContainsNoFutureEvents()
    {
        var now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var request=new ForecastRequest(now,60,false,5,[],Discontinuities:[now,now.AddSeconds(-5),now,now.AddSeconds(1)]);
        var json=JsonNode.Parse(Protocol.EncodeRequest(1,request))!;
        Assert.Equal("[1799999995,1800000000]",json["discontinuities_utc"]!.ToJsonString());
    }

    [Fact]
    public void Decodes_forecast_with_nulls_and_ignores_garbage()
    {
        Assert.Null(Protocol.Decode("not json"));
        Assert.Null(Protocol.Decode("[1,2]"));
        var m = Protocol.Decode("""
            {"protocol_version":1,"type":"forecast","id":4,"generated_at_utc":1.5,"context_end_utc":1,"source":"timestral",
             "scenario":"no_charge","reserve_pct":5,"level":50,"quality":{"state":"learning","observed_days":2.5,"coverage":0.9,"discharge_sessions":3},
             "crossing":{"status":"beyond_horizon","central_utc":null,"early_utc":100.0,"late_utc":null},
             "curve":[{"t":300,"p10":49,"p50":49.5,"p90":50}],"elapsed_ms":12}
            """.ReplaceLineEndings(""));
        var f = Assert.IsType<WorkerForecast>(m);
        Assert.Equal(4, f.Id);
        Assert.Equal("learning", f.Response.Quality.State);
        Assert.Null(f.Response.Crossing.CentralUtc);
        Assert.Equal(100.0, f.Response.Crossing.EarlyUtc);
        Assert.Equal(49.5, f.Response.Curve[0].P50);
    }

    [Fact]
    public void Wrong_version_is_an_error()
    {
        var e = Assert.IsType<WorkerError>(Protocol.Decode("""{"protocol_version":2,"type":"ready"}"""));
        Assert.Equal("bad_version", e.Code);
    }
}

/// <summary>Client contro un motore finto in Python (forecast/tests/fake_worker.py): niente PyTorch.</summary>
public class ForecastWorkerTests
{
    static readonly string Fake = FindUp(Path.Combine("forecast", "tests", "fake_worker.py"));

    internal static string FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
        return relative;
    }

    static WorkerLaunch Launch(string mode)
    {
        var python = Environment.GetEnvironmentVariable("BATTERY_FORECAST_TEST_PYTHON") ?? "py";
        string[] args = python == "py" ? ["-3", Fake, mode] : [Fake, mode];
        return new WorkerLaunch(python, args, Path.GetDirectoryName(Fake)!);
    }

    static ForecastRequest Request(double level = 50) =>
        new(DateTimeOffset.UtcNow, level, false, 5, [new(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300, level + 1, 0)]);

    static WorkerOptions Fast => new() { LoadTimeout = TimeSpan.FromSeconds(20), RequestTimeout = TimeSpan.FromSeconds(2), FirstBackoff = TimeSpan.Zero };

    [Fact]
    public async Task Answers_requests_in_order_and_stops_on_eof()
    {
        Assert.True(File.Exists(Fake), Fake);
        await using var w = new ForecastWorker(Launch("ok"), Fast);
        var a = await w.ForecastAsync(Request(50));
        var b = await w.ForecastAsync(Request(40));
        Assert.Equal(50, a.Level);
        Assert.Equal(40, b.Level);
        Assert.Equal(288, b.Curve.Count);
        Assert.Equal("fake", w.Info!.ModelId);
        Assert.True(w.IsWarm);
        await w.StopAsync();
        Assert.False(w.IsWarm);
    }

    [Fact]
    public async Task Partial_lines_noise_and_heavy_stderr_do_not_break_the_reply()
    {
        await using var w = new ForecastWorker(Launch("noisy"), Fast);
        var r = await w.ForecastAsync(Request(70));
        Assert.Equal(70, r.Level);
        Assert.True(w.StderrTail.Split('\n').Length <= 40);
    }

    [Fact]
    public async Task Timeout_kills_the_engine_and_next_request_restarts_it()
    {
        await using var w = new ForecastWorker(Launch("slow"), Fast);
        var e = await Assert.ThrowsAsync<ForecastWorkerException>(() => w.ForecastAsync(Request()));
        Assert.Equal("timeout", e.Code);
        Assert.False(w.IsWarm);
        await Assert.ThrowsAsync<ForecastWorkerException>(() => w.ForecastAsync(Request()));   // riparte, poi di nuovo timeout
    }

    [Fact]
    public async Task Crash_is_reported_as_exited_and_backoff_blocks_immediate_restart()
    {
        var opts = Fast with { FirstBackoff = TimeSpan.FromMinutes(5) };
        await using var w = new ForecastWorker(Launch("crash"), opts);
        var e = await Assert.ThrowsAsync<ForecastWorkerException>(() => w.ForecastAsync(Request()));
        Assert.Equal("exited", e.Code);
        var again = await Assert.ThrowsAsync<ForecastWorkerException>(() => w.ForecastAsync(Request()));
        Assert.Equal("backoff", again.Code);
    }

    [Fact]
    public async Task Missing_model_is_a_clear_non_retryable_error()
    {
        await using var w = new ForecastWorker(Launch("badmodel"), Fast);
        var e = await Assert.ThrowsAsync<ForecastWorkerException>(() => w.ForecastAsync(Request()));
        Assert.Equal("model_unavailable", e.Code);
        Assert.False(e.Retryable);
    }

    /// <summary>
    /// Il vero motore Timestral come lo avvia l'app (venv del repository o pacchetto), con una settimana di storia
    /// a passi di 5 minuti. Salta se i pesi non ci sono (CI senza modello).
    /// </summary>
    [Fact]
    public async Task Real_engine_answers_a_week_of_history_like_the_app_sends_it()
    {
        if (EngineLocator.Find(AppContext.BaseDirectory) is not { } launch
            || !File.Exists(Path.Combine(launch.Arguments[launch.Arguments.ToList().IndexOf("--model") + 1], "config.json")))
            return;
        await using var w = new ForecastWorker(launch, new WorkerOptions { LoadTimeout = TimeSpan.FromSeconds(90), RequestTimeout = TimeSpan.FromSeconds(30) });
        var now = DateTimeOffset.UtcNow;
        var start = now.ToUnixTimeSeconds() - 7 * 86400;
        var history = Enumerable.Range(0, 7 * 288).Select(i =>
        {
            var plugged = i % 288 >= 150;
            return new ModelSample(start + 300 * i, plugged ? 90 : Math.Max(5, 100 - (i % 288) * 0.5), plugged ? 1 : 0);
        }).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        history[10]=history[10] with { Observed=false };
        var r = await w.ForecastAsync(new ForecastRequest(now, 80, false, 5, history,Discontinuities:[now.AddMinutes(-10)]));
        Assert.Equal("timestral", r.Source);
        Assert.Equal(288, r.Curve.Count);
        var held = await w.ForecastAsync(new ForecastRequest(now, 80, true, 5, history));      // collegato, carica ferma
        Assert.Equal("no_charge", held.Scenario);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.Elapsed}");
    }

    [Fact]
    public async Task Missing_executable_is_not_found()
    {
        await using var w = new ForecastWorker(new WorkerLaunch(@"C:\nope\missing.exe", [], Path.GetTempPath()), Fast);
        var e = await Assert.ThrowsAsync<ForecastWorkerException>(() => w.ForecastAsync(Request()));
        Assert.Equal("not_found", e.Code);
    }
}
