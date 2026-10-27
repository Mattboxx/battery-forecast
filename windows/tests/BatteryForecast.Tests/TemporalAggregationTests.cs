using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;
using Microsoft.Data.Sqlite;

namespace BatteryForecast.Tests;

public class TemporalAggregationTests : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),$"bf-time-{Guid.NewGuid():N}.db");
    readonly DateTimeOffset zero=DateTimeOffset.FromUnixTimeSeconds(1800000000);

    [Fact]
    public void DiagnosticBurstHasItsActualDurationNotElevenEqualVotes()
    {
        using var store=new HistoryStore(path);
        store.AppendContext(new(zero,MemoryPct:20,CollectionKind:"scheduled"));
        store.Append(new(zero,ChargeState.Discharging,80,false),"scheduled");
        store.AppendContext(new(zero.AddSeconds(60),CpuPct:20,MemoryPct:80,CpuIntervalStartUtc:zero));
        for(int t=61;t<=70;t++)
        {
            store.AppendContext(new(zero.AddSeconds(t),CpuPct:80,MemoryPct:80,CpuIntervalStartUtc:zero.AddSeconds(t-1),CollectionKind:"diagnostic"));
            store.Append(new(zero.AddSeconds(t),ChargeState.Discharging,20,false),"diagnostic");
        }
        // The battery transition is first observed at 61s, not at 60s.
        var context=store.ModelCovariates(zero,zero.AddSeconds(120));
        Assert.Equal((20d*60+80*10)/70,Assert.Single(context["cpu_pct"]).Value!.Value,8);
        Assert.Equal(50,Assert.Single(context["memory_pct"]).Value!.Value,8);
        Assert.Equal((80d*61+20*59)/120,Assert.Single(store.ModelSamples(zero,zero.AddSeconds(120))).Level!.Value,8);
        using var csv=new StringWriter(); store.ExportContextCsv(csv);
        Assert.Contains("cpu_interval_start_unix",csv.ToString()); Assert.Contains("diagnostic",csv.ToString());
    }

    [Fact]
    public void CpuWindowsSplitAtBinEdgesAndFutureMeasurementsCannotFillPastBins()
    {
        using var store=new HistoryStore(path);
        store.AppendContext(new(zero.AddSeconds(290),CpuPct:10,CpuIntervalStartUtc:zero.AddSeconds(280)));
        store.AppendContext(new(zero.AddSeconds(310),CpuPct:70,CpuIntervalStartUtc:zero.AddSeconds(290)));
        var rows=store.ModelCovariates(zero,zero.AddSeconds(310))["cpu_pct"];
        Assert.Equal(40,rows[0].Value);Assert.Equal(70,rows[1].Value);
        Assert.Equal(10,Assert.Single(store.ModelCovariates(zero,zero.AddSeconds(300))["cpu_pct"]).Value);
    }

    [Fact]
    public void ExactObservationTimeExcludesLaterReadingsWithinTheSameSecond()
    {
        using var store=new HistoryStore(path);
        store.AppendContext(new(zero.AddMilliseconds(900),MemoryPct:90));
        store.Append(new(zero.AddMilliseconds(900),ChargeState.Discharging,90,false));
        Assert.Empty(store.ModelCovariates(zero,zero.AddMilliseconds(500))["memory_pct"]);
        Assert.Empty(store.ModelSamples(zero,zero.AddMilliseconds(500)));
    }

    [Fact]
    public void LegacyCpuIsRetainedForHistoryButHasNoInventedMeasurementInterval()
    {
        using(var store=new HistoryStore(path)) store.AppendContext(new(zero,CpuPct:25));
        using(var db=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText=LegacySchema.DropTemporalMetadata+"PRAGMA user_version=5;";cmd.ExecuteNonQuery();
        }
        using var migrated=new HistoryStore(path);
        Assert.Null(Assert.Single(migrated.ModelCovariates(zero,zero.AddSeconds(60))["cpu_pct"]).Value);
        Assert.Equal(25,Assert.Single(migrated.ModelCovariates(zero,zero.AddSeconds(60),includeLegacyCpu:true)["cpu_pct"]).Value);
        using var csv=new StringWriter();Assert.Equal(1,migrated.ExportContextCsv(csv));
        Assert.Contains("legacy",csv.ToString()); Assert.Contains(",25,",csv.ToString());
    }

    [Fact]
    public void CarryExpiresAndCpuMeanAcrossAnInterruptionIsRejected()
    {
        using var store=new HistoryStore(path);
        store.AppendContext(new(zero,MemoryPct:20));
        store.AppendContext(new(zero.AddSeconds(600),MemoryPct:80));
        var values=store.ModelCovariates(zero,zero.AddSeconds(600))["memory_pct"];
        Assert.Equal(new[]{zero.ToUnixTimeSeconds(),zero.AddSeconds(600).ToUnixTimeSeconds()},values.Select(v=>v.Ts));
        store.AppendEvent(zero.AddSeconds(45),"resume");
        store.AppendContext(new(zero.AddSeconds(60),CpuPct:50,CpuIntervalStartUtc:zero));
        Assert.All(store.ModelCovariates(zero,zero.AddSeconds(60))["cpu_pct"],v=>Assert.Null(v.Value));
    }

    public void Dispose() { foreach(var f in new[]{path,path+"-wal",path+"-shm"})File.Delete(f); }
}
