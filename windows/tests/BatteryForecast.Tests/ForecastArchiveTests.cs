using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Presentation;
using BatteryForecast.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using BatteryForecast.Core;

namespace BatteryForecast.Tests;

public class ForecastArchiveTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"bf-archive-{Guid.NewGuid():N}.db");
    static ForecastResponse Response(DateTimeOffset origin) => new()
    {
        ContextEndUtc = origin.ToUnixTimeSeconds(), GeneratedAtUtc = origin.AddSeconds(10).ToUnixTimeSeconds(),
        Level = 90, Source = "timestral", Scenario = "no_charge", ReservePct = 5, ModelRevision = "test",
        Curve = [new(origin.AddMinutes(5).ToUnixTimeSeconds(), 83,85,87), new(origin.AddMinutes(10).ToUnixTimeSeconds(),78,80,82)],
    };
    static void Save(HistoryStore store, ForecastResponse r, DateTimeOffset issued) => store.RecordForecast(
        DateTimeOffset.FromUnixTimeSeconds((long)r.ContextEndUtc), r.Source, r.Level, r.ReservePct,
        "beyond_horizon", null,null,null,"learning",r.ModelId,r.ModelRevision,0,snapshot:r,issuedAt:issued);

    [Fact]
    public void Upgrade_preserves_the_genuine_past_cache_once_without_recalculating_it()
    {
        using var store = new HistoryStore(path);
        var clock = new FakeTimeProvider(Samples.T0.AddSeconds(15));
        var cachePath = path + ".json";
        try
        {
            File.WriteAllText(cachePath, JsonSerializer.Serialize(Response(Samples.T0), Protocol.Json));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var collector = new Collector(new CachedBattery(clock),store,clock);
                using var coordinator = new ForecastCoordinator(collector,store,null,() => new Settings { Refresh=RefreshPolicy.OnOpen },cachePath,clock);
                coordinator.Start();
            }
            var saved = Assert.Single(store.ForecastSnapshots(Samples.T0,Samples.T0.AddMinutes(1)));
            Assert.Equal(Samples.T0.AddSeconds(10),saved.IssuedAt);
            Assert.Equal(85,saved.Response.Curve[0].P50);
        }
        finally { File.Delete(cachePath); }
    }

    sealed class CachedBattery(FakeTimeProvider clock) : IBatteryProvider
    { public BatterySample Read() => new(clock.GetUtcNow(),ChargeState.Discharging,90,false); }

    [Fact]
    public void Migration_four_preserves_existing_history_and_does_not_invent_forecasts()
    {
        using (var store = new HistoryStore(path)) store.Append(Samples.Discharging(0,90));
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open(); using var cmd=db.CreateCommand();
            cmd.CommandText="DROP TABLE forecast_snapshots;PRAGMA user_version=4;";cmd.ExecuteNonQuery();
        }
        using var migrated=new HistoryStore(path);
        Assert.Single(migrated.Samples(Samples.T0));
        Assert.Empty(migrated.ForecastSnapshots(Samples.T0,Samples.T0.AddDays(1)));
    }

    [Fact]
    public void Saved_curve_is_immutable_and_new_calculations_do_not_replace_it()
    {
        using var store=new HistoryStore(path);
        var r=Response(Samples.T0);
        Save(store,r,Samples.T0.AddSeconds(10));
        r.Curve[0]=r.Curve[0] with { P50=20 };
        Save(store,r,Samples.T0.AddSeconds(20));
        var snapshots=store.ForecastSnapshots(Samples.T0,Samples.T0.AddMinutes(1));
        Assert.Equal(2,snapshots.Count);
        Assert.Equal(20,snapshots[0].Response.Curve[0].P50);
        Assert.Equal(85,snapshots[1].Response.Curve[0].P50);
        Assert.Equal(Samples.T0.AddSeconds(10),snapshots[1].IssuedAt);
        Assert.Single(store.ForecastSnapshots(Samples.T0,Samples.T0.AddSeconds(15)));
    }

    [Fact]
    public void Retention_and_delete_history_include_saved_forecast_curves()
    {
        using var store=new HistoryStore(path);
        Save(store,Response(Samples.T0.AddDays(-40)),Samples.T0.AddDays(-40));
        Save(store,Response(Samples.T0),Samples.T0);
        store.Prune(Samples.T0,30);
        Assert.Single(store.ForecastSnapshots(Samples.T0.AddDays(-100),Samples.T0));
        store.Wipe();
        Assert.Empty(store.ForecastSnapshots(Samples.T0.AddDays(-100),Samples.T0));
    }

    [Fact]
    public void Comparison_uses_the_original_issued_curve_only_after_issuance_and_flags_different_conditions()
    {
        var saved=new ForecastSnapshot(1,Samples.T0.AddMinutes(2),"timestral",Response(Samples.T0));
        Assert.Null(TrendComparison.At(saved,Samples.Discharging(1,89),[],[]));
        var actual=new[] {Samples.Discharging(0,90),Samples.OnAc(10,70)};
        var point=TrendComparison.Latest(saved,actual,[])!;
        Assert.Equal(80,point.Predicted.P50);
        Assert.Equal(-10,point.Difference);
        Assert.True(point.ScenarioChanged);
        Assert.True(point.Interrupted);
        Assert.Null(TrendComparison.At(saved,Samples.Discharging(11,79),actual,[])); // beyond the saved horizon
    }

    public void Dispose() { foreach(var p in new[] {path,path+"-wal",path+"-shm"}) File.Delete(p); }
}
