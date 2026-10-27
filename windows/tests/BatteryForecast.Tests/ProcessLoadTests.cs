using BatteryForecast.Core.Telemetry;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.Sqlite;

namespace BatteryForecast.Tests;

public class ProcessLoadTests
{
    sealed class Counters : IProcessCounterSource
    {
        internal ProcessCounter[] Rows=[];
        public IReadOnlyList<ProcessCounter> Read()=>Rows;
    }
    [Fact]
    public void PerExecutableLoadGroupsProcessesAndUsesTheirMeasuredCpuIntervals()
    {
        var source=new Counters { Rows=[new(1,10,"Chrome",TimeSpan.Zero,100),new(2,11,"chrome",TimeSpan.FromSeconds(10),200)] };
        var sampler=new ProcessLoadSampler(source,3);var at=DateTimeOffset.UnixEpoch;
        var first=sampler.Read(at)!;Assert.Null(first.CpuPct);Assert.Null(first.Loads[0].CpuPct);
        Assert.Equal(300,Assert.Single(first.Loads).WorkingSetMB);
        source.Rows=[new(1,10,"Chrome",TimeSpan.FromSeconds(6),110),new(2,11,"chrome",TimeSpan.FromSeconds(13),215)];
        var second=sampler.Read(at.AddMinutes(1))!;
        Assert.Equal(5,Assert.Single(second.Loads).CpuPct);Assert.Equal(at,second.IntervalStart);
        Assert.Equal(first.Profile,second.Profile);
        Assert.Equal(ProcessFeatures.Id("Chrome"),ProcessFeatures.Id(" chrome "));
    }
    [Fact]
    public void PidReuseLongGapsAndFirstReadsNeverInventCpu()
    {
        var source=new Counters { Rows=[new(1,10,"app",TimeSpan.FromSeconds(10),20)] };
        var sampler=new ProcessLoadSampler(source,4);var at=DateTimeOffset.UnixEpoch;
        sampler.Read(at);Assert.Null(sampler.Read(at.AddMilliseconds(500)));
        source.Rows=[new(1,20,"app",TimeSpan.FromSeconds(1000),20)];
        Assert.Null(sampler.Read(at.AddMinutes(1))!.CpuPct);
        Assert.Null(sampler.Read(at.AddHours(8))!.CpuPct);
        sampler.Reset();Assert.Null(sampler.Read(at.AddHours(8).AddMinutes(1))!.IntervalStart);
        source.Rows=[];Assert.Null(sampler.Read(at.AddHours(8).AddMinutes(2))!.WorkingSetMB);
    }
    [Fact]
    public void PersistenceAndModelChannelsKeepAppIdentitiesWithoutNamesInTheProtocol()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-app-load-{Guid.NewGuid():N}.db");
        var at=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        try
        {
            using var store=new HistoryStore(path);
            store.AppendContext(new(at.AddMinutes(1),ProcessLoads:[new("chrome",10,100),new("editor",5,200)],ProcessCpuPct:15,ProcessMemoryMB:300,
                WorkloadProfile:123,ProcessIntervalStartUtc:at));
            store.AppendContext(new(at.AddMinutes(2),ProcessLoads:[new("chrome",40,120)],ProcessCpuPct:40,ProcessMemoryMB:120,
                WorkloadProfile:124,ProcessIntervalStartUtc:at.AddMinutes(1)));
            var channels=store.ModelCovariates(at,at.AddMinutes(2),includeApps:true);
            Assert.Equal(25,Assert.Single(channels[ProcessFeatures.Cpu("chrome")]).Value);
            Assert.False(channels.ContainsKey(ProcessFeatures.Cpu("editor"))); // not a current observed group
            var request=Protocol.EncodeRequest(1,new(at.AddMinutes(2),80,false,5,[],channels));
            Assert.DoesNotContain("chrome",request);Assert.Contains(ProcessFeatures.Cpu("chrome"),request);
            using var csv=new StringWriter();Assert.Equal(2,store.ExportContextCsv(csv));
            Assert.Contains("processes_json",csv.ToString());Assert.Contains("\"\"Name\"\":\"\"chrome\"\"",csv.ToString());
            store.Wipe();Assert.Empty(store.ModelCovariates(at,at.AddMinutes(2),includeApps:true)["process_cpu_pct"]);
        }
        finally { foreach(var p in new[]{path,path+"-wal",path+"-shm"})File.Delete(p); }
    }
    [Fact]
    public void SchemaSixMigrationPreservesHistoryAndDoesNotInventAppNames()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-app-schema-{Guid.NewGuid():N}.db");var at=DateTimeOffset.UtcNow;
        try
        {
            using(var store=new HistoryStore(path)) store.AppendContext(new(at,MemoryPct:60));
            using(var raw=new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();using var cmd=raw.CreateCommand();cmd.CommandText=LegacySchema.DropProcessMetadata+"PRAGMA user_version=6;";cmd.ExecuteNonQuery();
            }
            using var migrated=new HistoryStore(path);
            Assert.Equal(1,migrated.ContextSummary(DateTimeOffset.MinValue).Samples);
            Assert.Equal(0,migrated.ContextSummary(DateTimeOffset.MinValue).Observations["workload_profile"]);
            Assert.DoesNotContain(migrated.ModelCovariates(DateTimeOffset.MinValue,at.AddMinutes(1),includeApps:true).Keys,k=>k.StartsWith("app_",StringComparison.Ordinal));
            using var csv=new StringWriter();migrated.ExportContextCsv(csv);Assert.DoesNotContain("\"\"Name\"\"",csv.ToString());
        }
        finally { foreach(var p in new[]{path,path+"-wal",path+"-shm"})File.Delete(p); }
    }
    [Fact]
    public void AppConsumptionAssociationUsesComparableWholePcPowerNotInventedPerAppWatts()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-app-power-{Guid.NewGuid():N}.db");var at=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        try
        {
            using var store=new HistoryStore(path);SystemSample? last=null;
            for(int i=0;i<6;i++)
            {
                var t=at.AddMinutes(i*5+1);
                last=new(t,CpuPct:25,DischargeW:10+i,DisplayState:1,CpuIntervalStartUtc:t.AddMinutes(-1),
                    ProcessLoads:[new("chrome",10,100)],ProcessCpuPct:10,ProcessMemoryMB:100,WorkloadProfile:123,ProcessIntervalStartUtc:t.AddMinutes(-1));
                store.Append(new(t,Core.Battery.ChargeState.Discharging,80-i,false,DesignMWh:50000));store.AppendContext(last);
            }
            var evidence=Assert.Single(store.ProcessAssociations(last!,at,last!.TimestampUtc));
            Assert.Equal(6,evidence.Windows);Assert.Equal(12.5,evidence.MedianPcWatts);
            Assert.Null(Assert.Single(store.ProcessAssociations(last with { WorkloadProfile=124 },at,last.TimestampUtc)).MedianPcWatts);
            store.AppendEvent(at.AddSeconds(100),"resume");
            var interrupted=Assert.Single(store.ProcessAssociations(last,at,last.TimestampUtc));
            Assert.Equal(5,interrupted.Windows);Assert.Null(interrupted.MedianPcWatts);
        }
        finally { foreach(var p in new[]{path,path+"-wal",path+"-shm"})File.Delete(p); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessOptOutAlsoStopsUsingStoredAppChannels(bool enabled)
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-app-optout-{Guid.NewGuid():N}.db");
        var clock=new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            using var store=new HistoryStore(path);
            store.AppendContext(new(clock.GetUtcNow().AddMinutes(-1),ProcessLoads:[new("chrome",10,100)],ProcessCpuPct:10,ProcessMemoryMB:100,
                WorkloadProfile:123,ProcessIntervalStartUtc:clock.GetUtcNow().AddMinutes(-2)));
            using var collector=new Collector(new FakeBattery(clock),store,clock);
            var engine=new CapturedEngine();
            using var coordinator=new ForecastCoordinator(collector,store,engine,()=>new Settings { CollectProcesses=enabled },clock:clock);
            coordinator.Start();
            Assert.True(SpinWait.SpinUntil(()=>engine.Request is not null,TimeSpan.FromSeconds(5)));
            Assert.Equal(enabled,engine.Request!.PastCovariates!.ContainsKey("workload_profile"));
            Assert.Equal(enabled,engine.Request.PastCovariates.ContainsKey(ProcessFeatures.Cpu("chrome")));
            Assert.False(new Settings().CollectProcesses);
        }
        finally { foreach(var p in new[]{path,path+"-wal",path+"-shm"})File.Delete(p); }
    }
    sealed class CapturedEngine : IForecastEngine
    {
        internal ForecastRequest? Request;
        public WorkerReady? Info=>new("test","test",0); public bool IsWarm=>true;
        public Task WarmUpAsync(CancellationToken ct=default)=>Task.CompletedTask;public Task StopAsync()=>Task.CompletedTask;
        public Task<ForecastResponse> ForecastAsync(ForecastRequest r,CancellationToken ct=default)
        { Request=r;return Task.FromResult(new ForecastResponse { ContextEndUtc=r.Now.ToUnixTimeSeconds(),GeneratedAtUtc=r.Now.ToUnixTimeSeconds(),Level=r.Level,Source="test" }); }
    }
}
