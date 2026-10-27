using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;
using Microsoft.Data.Sqlite;

namespace BatteryForecast.Tests;
public class TelemetryTests
{
    [Fact]
    public void CpuCounterUsesDeltasAndRejectsResetOrInvalidCounters()
    {
        Assert.Equal(75,SensorMath.CpuPercent(100,1000,125,1100));
        Assert.Null(SensorMath.CpuPercent(100,1000,200,1000));
        Assert.Null(SensorMath.CpuPercent(100,1000,50,1100));
        Assert.Null(SensorMath.CpuPercent(100,1000,300,1100));
    }
    [Fact]
    public void TemperaturesKeepUnitsAndUnavailableSentinels()
    {
        Assert.Equal(26.9,SensorMath.KelvinTenthsToCelsius(3000));
        Assert.Null(SensorMath.KelvinTenthsToCelsius(0));
        Assert.Null(SensorMath.KelvinTenthsToCelsius(uint.MaxValue));
    }
    [Fact]
    public void SchemaTwoMigratesWithoutLosingBatteryHistoryAndExportsContext()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-context-{Guid.NewGuid():N}.db");
        try
        {
            var now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
            using(var initial=new HistoryStore(path)) initial.Append(new(now,ChargeState.Discharging,80,false));
            using(var raw=new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();using var cmd=raw.CreateCommand();cmd.CommandText=LegacySchema.DropTemporalMetadata+"DROP TABLE context_samples;DROP TABLE forecast_snapshots;PRAGMA user_version=2;";cmd.ExecuteNonQuery();
            }
            using(var store=new HistoryStore(path))
            {
                Assert.Single(store.Samples(now));
                store.AppendContext(new(now.AddMinutes(1),CpuPct:30,MemoryPct:60,DischargeW:12.5,PowerProfile:0,CpuIntervalStartUtc:now));
                store.AppendContext(new(now.AddMinutes(2),CpuPct:50,MemoryPct:62,DischargeW:13.5,PowerProfile:2,CpuIntervalStartUtc:now.AddMinutes(1)));
                var data=store.ModelCovariates(now,now.AddMinutes(5));
                Assert.Equal(40,Assert.Single(data["cpu_pct"]).Value);
                Assert.Null(Assert.Single(data["cpu_temp_c"]).Value);
                Assert.Null(Assert.Single(data["power_profile"]).Value);
                var request=new ForecastRequest(now,80,false,5,store.ModelSamples(now),data);
                Assert.Contains("past_covariates",Protocol.EncodeRequest(1,request));
                using var writer=new StringWriter();Assert.Equal(2,store.ExportContextCsv(writer));
                Assert.Contains("cpu_pct",writer.ToString());Assert.Contains("12.5",writer.ToString());
                store.Wipe();Assert.Empty(store.ModelCovariates(now,now.AddMinutes(5))["cpu_pct"]);
            }
        }
        finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"})File.Delete(f); }
    }
    [Fact]
    public void ContextCanBeDisabledWithoutStoppingBatteryCollection()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-context-{Guid.NewGuid():N}.db");
        try
        {
            using var store=new HistoryStore(path);
            var context=new FakeContext();
            using var collector=new Collector(new FakeBattery(),store,context:context,collectContext:()=>false);
            collector.Sample();Assert.Equal(0,context.Calls);Assert.Null(collector.CurrentContext);
            Assert.Single(store.Samples(DateTimeOffset.MinValue));
        }
        finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"})File.Delete(f); }
    }
    sealed class FakeBattery:IBatteryProvider { public BatterySample Read()=>new(DateTimeOffset.UtcNow,ChargeState.Discharging,70,false); }
    sealed class FakeContext:ISystemContextProvider { public int Calls; public SystemSample Read(BatterySample b){Calls++;return new(b.TimestampUtc,CpuPct:40);} }
}
