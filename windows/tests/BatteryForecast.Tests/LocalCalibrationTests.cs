using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;
using BatteryForecast.Core;
using Microsoft.Extensions.Time.Testing;

namespace BatteryForecast.Tests;

public class LocalCalibrationTests
{
    static readonly DateTimeOffset Zero=DateTimeOffset.FromUnixTimeSeconds(1800000000);
    sealed record Data(List<ForecastSnapshot> Saved,List<BatterySample> Actual,List<DateTimeOffset> Breaks,List<CalibrationContext> Context,DateTimeOffset Now);
    static ForecastConditions Conditions(string? key=null,bool charge=false)=>new()
    { OnAc=charge,DesignMWh=50000,FullMWh=50000,AppContext=key is not null,WorkloadKey=key,CpuBand=key is null?null:2 };
    static ForecastResponse Forecast(DateTimeOffset origin,double level=90,string? key=null,bool charge=false)
    {
        var points=Enumerable.Range(1,288).Select(i=>
        {
            var h=i/12d;
            return new CurvePointDto(origin.AddMinutes(i*5).ToUnixTimeSeconds(),
                Math.Clamp(level+(charge?8:-12)*h,0,100),Math.Clamp(level+(charge?10:-10)*h,0,100),Math.Clamp(level+(charge?12:-8)*h,0,100));
        }).ToList();
        return new() { Source="timestral",ModelRevision="rev",TimeBasis="active_use",ContextEndUtc=origin.ToUnixTimeSeconds(),GeneratedAtUtc=origin.ToUnixTimeSeconds(),
            Level=level,Quality=new() { State="ok" },ReservePct=charge?80:5,TargetPct=charge?80:null,Scenario=charge?"charge":"no_charge",Curve=points,Conditions=Conditions(key,charge),
            Crossing=new() { Status="within_horizon",CentralUtc=origin.AddHours(charge?(80-level)/10:(level-5)/10).ToUnixTimeSeconds() } };
    }
    static Data History(double ratio=1.2,int sessions=3,string? key=null,int startDay=0)
    {
        var saved=new List<ForecastSnapshot>();var actual=new List<BatterySample>();var breaks=new List<DateTimeOffset>();var context=new List<CalibrationContext>();
        int total=0;
        for(int session=0;session<sessions;session++)
        {
            var start=Zero.AddDays(startDay+session);breaks.Add(start);
            int count=sessions==1?8:session<2?3:2;
            for(int minute=0;minute<=count*30;minute++)
            {
                double level=90-ratio*10*minute/60;
                actual.Add(new(start.AddMinutes(minute),ChargeState.Discharging,level,false,FullChargeMWh:50000,DesignMWh:50000));
                if(key is not null) context.Add(new(start.AddMinutes(minute),2,null,null,key));
            }
            for(int i=0;i<count;i++)
            {
                var origin=start.AddMinutes(i*30);var response=Forecast(origin,90-ratio*10*i*.5,key);
                saved.Add(new(++total,origin.AddSeconds(1),"timestral",response));
            }
        }
        return new(saved,actual,breaks,context,actual[^1].TimestampUtc.AddMinutes(10));
    }
    static CalibrationInfo Learn(Data data,ForecastResponse? current=null,DateTimeOffset? reset=null)=>LocalCalibration.Learn(
        current??Forecast(data.Now),data.Saved,data.Actual,data.Breaks,data.Context,data.Now,reset);

    [Fact]
    public void EightIndependentWindowsAcrossThreeSessionsLearnTheObservedRateBias()
    {
        var data=History();var report=Learn(data);
        Assert.True(report.Ready);Assert.Equal(8,report.Windows);Assert.Equal(3,report.Sessions);Assert.Equal(1.2,report.Factor,8);
    }
    [Fact]
    public void RepeatedOverlappingForecastsDoNotMultiplyEvidenceAndOneSessionIsNotEnough()
    {
        var data=History();var repeated=data with { Saved=data.Saved.SelectMany(s=>Enumerable.Range(0,5).Select(i=>s with { Id=s.Id*10+i })).ToList() };
        Assert.Equal(8,Learn(repeated).Windows);
        var single=Learn(History(sessions:1));Assert.Equal(8,single.Windows);Assert.Equal(1,single.Sessions);Assert.False(single.Ready);
    }
    [Fact]
    public void NoFutureObservationsResetOrSleepCanBecomeTrainingEvidence()
    {
        var data=History();var beforeSecondDay=Zero.AddDays(1);
        var early=LocalCalibration.Learn(Forecast(beforeSecondDay),data.Saved,data.Actual,data.Breaks,data.Context,beforeSecondDay);
        Assert.Equal(3,early.Windows);Assert.False(early.Ready);
        Assert.Equal(0,Learn(data,reset:data.Now.AddSeconds(-1)).Windows);
        data.Breaks.Add(Zero.AddMinutes(15));Assert.Equal(7,Learn(data).Windows);
        Assert.False(Learn(data).Ready);
    }
    [Fact]
    public void WorkloadAndPerAppCpuBandsKeepSeparateBiasesInsteadOfPoolingDifferentApps()
    {
        var low=History(1.2,key:"profile|abcdef012345:1");var high=History(.8,key:"profile|abcdef012345:2",startDay:3);
        var all=new Data([..low.Saved,..high.Saved],[..low.Actual,..high.Actual],[..low.Breaks,..high.Breaks],[..low.Context,..high.Context],high.Now);
        Assert.Equal(1.2,Learn(all,Forecast(all.Now,key:"profile|abcdef012345:1")).Factor,8);
        Assert.Equal(.8,Learn(all,Forecast(all.Now,key:"profile|abcdef012345:2")).Factor,8);
        Assert.False(Learn(all,Forecast(all.Now,key:"profile|ffffff012345:1")).Ready);
        Assert.NotEqual(ForecastConditions.AppKey(1,[new("editor",10,100)]),ForecastConditions.AppKey(1,[new("editor",30,100)]));
    }
    [Fact]
    public void ModelRevisionBatteryAndUnexpectedWorkloadChangesAreNotLearnedAsForecastErrors()
    {
        var data=History(key:"app:1");
        Assert.Equal(0,Learn(data,Forecast(data.Now,key:"app:1") with { ModelRevision="other" }).Windows);
        Assert.Equal(0,Learn(data,Forecast(data.Now,key:"app:1") with { Quality=new() { State="insufficient" } }).Windows);
        Assert.Equal(0,Learn(data with { Saved=data.Saved.Select(s=>new ForecastSnapshot(s.Id,s.IssuedAt,s.Source,s.Response with { Quality=new() })).ToList() },Forecast(data.Now,key:"app:1")).Windows);
        Assert.Equal(0,Learn(data,Forecast(data.Now,key:"app:1") with { Conditions=Conditions("app:1") with { DesignMWh=60000 } }).Windows);
        var changed=data with { Context=data.Context.Select(c=>c with { WorkloadKey="different" }).ToList() };
        Assert.Equal(0,Learn(changed,Forecast(data.Now,key:"app:1")).Windows);
    }
    [Theory]
    [InlineData(.6,.75)]
    [InlineData(2,1.25)]
    public void LearnedCorrectionIsBounded(double ratio,double expected)
    {
        var data=History(ratio);var report=Learn(data);
        Assert.True(report.Ready);
        Assert.Equal(expected,report.Factor);
    }
    [Fact]
    public void TimeWarpPreservesRawCurveQuantilesAndCanBeDisabledWithoutCompounding()
    {
        var raw=Forecast(Zero);var report=new CalibrationInfo { Enabled=true,Ready=true,Factor=1.2,Windows=8,Sessions=3 };
        var adjusted=LocalCalibration.Apply(raw,report);
        Assert.True(adjusted.Curve.Count<raw.Curve.Count);Assert.Equal(raw.Curve.ToArray(),adjusted.RawCurve);
        Assert.True(adjusted.Crossing.CentralUtc<raw.Crossing.CentralUtc);
        Assert.All(adjusted.Curve,p=>Assert.True(p.P10<=p.P50&&p.P50<=p.P90&&p.P10>=0&&p.P90<=100));
        var twice=LocalCalibration.Apply(adjusted,report);Assert.Equal(adjusted.Curve,twice.Curve);
        var disabled=LocalCalibration.Apply(adjusted,report with { Enabled=false });
        Assert.Equal(raw.Curve,disabled.Curve);Assert.Equal(raw.Crossing,disabled.Crossing);
        Assert.Equal(288,raw.Curve.Count); // original input unchanged
    }
    [Fact]
    public void SlowCorrectionStillReachesReserveRatherThanLeavingAClippedResidualForever()
    {
        var adjusted=LocalCalibration.Apply(Forecast(Zero),new() { Enabled=true,Ready=true,Factor=.75 });
        Assert.Equal(288,adjusted.Curve.Count);Assert.NotNull(adjusted.Crossing.CentralUtc);
        Assert.Equal(0,adjusted.Curve[^1].P50);
        var charge=LocalCalibration.Apply(Forecast(Zero,40,charge:true),new() { Enabled=true,Ready=true,Factor=1.25 });
        Assert.NotNull(charge.Crossing.CentralUtc);Assert.True(charge.Crossing.EarlyUtc<=charge.Crossing.CentralUtc&&charge.Crossing.CentralUtc<=charge.Crossing.LateUtc);
    }
    [Fact]
    public void LearningUsesOriginalCurveRatherThanRelearningItsOwnPreviousCorrection()
    {
        var data=History();var report=Learn(data);
        var corrected=data with { Saved=data.Saved.Select(s=>new ForecastSnapshot(s.Id,s.IssuedAt,s.Source,LocalCalibration.Apply(s.Response,report))).ToList() };
        Assert.Equal(1.2,Learn(corrected).Factor,8);
    }
    [Fact]
    public void ChargeWindowsLearnTheSameRateRelationWithoutUsingIdleOrHeldPeriods()
    {
        var data=History(1.1);
        data=data with
        {
            Actual=data.Actual.Select(s=>s with { LevelPct=100-s.LevelPct,OnAc=true,State=ChargeState.Charging }).ToList(),
            Saved=data.Saved.Select(s=>new ForecastSnapshot(s.Id,s.IssuedAt,"timestral:charge",Forecast(s.View.Generated,100-s.Response.Level,charge:true))).ToList(),
        };
        Assert.Equal(1.1,Learn(data,Forecast(data.Now,40,charge:true)).Factor,8);
        var held=data with { Actual=data.Actual.Select(s=>s with { State=ChargeState.Idle }).ToList() };
        Assert.False(Learn(held,Forecast(data.Now,40,charge:true)).Ready);
    }
    [Fact]
    public void CoordinatorLearnsFromArchiveAndSavesBothIssuedAndOriginalCurves()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-learn-{Guid.NewGuid():N}.db");var data=History();
        var clock=new FakeTimeProvider(data.Now);
        try
        {
            using var store=new HistoryStore(path);
            foreach(var sample in data.Actual) store.Append(sample);
            foreach(var saved in data.Saved) store.PreserveCachedForecast(saved.Response,saved.IssuedAt);
            // More than the read limit: rapid manual requests must not displace the older independent sessions.
            for(int i=0;i<250;i++)
            {
                var origin=data.Now.AddMinutes(-20).AddSeconds(-i);
                store.PreserveCachedForecast(Forecast(origin),origin.AddSeconds(1));
            }
            using var collector=new Collector(new CalibrationBattery(clock),store,clock);
            using var coordinator=new ForecastCoordinator(collector,store,new CalibrationEngine(),()=>new Settings { UseSystemContext=false },clock:clock);
            coordinator.Start();
            Assert.True(SpinWait.SpinUntil(()=>coordinator.State is { Updating:false,Forecast.Calibration.Ready:true },TimeSpan.FromSeconds(5)));
            Assert.Equal(1.2,coordinator.State!.Forecast!.Calibration!.Factor,8);
            var latest=Assert.Single(store.ForecastSnapshots(data.Now.AddSeconds(-1),data.Now));
            Assert.NotNull(latest.Response.RawCurve);Assert.True(latest.Response.Curve.Count<latest.Response.RawCurve.Count);
            Assert.Equal(8,latest.Response.Calibration!.Windows);
        }
        finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"})File.Delete(f); }
    }
    sealed class CalibrationBattery(FakeTimeProvider clock):IBatteryProvider
    {
        public BatterySample Read()=>new(clock.GetUtcNow(),ChargeState.Discharging,60,false,FullChargeMWh:50000,DesignMWh:50000);
    }
    sealed class CalibrationEngine:IForecastEngine
    {
        public WorkerReady? Info=>new("test","rev",0);public bool IsWarm=>true;
        public Task WarmUpAsync(CancellationToken ct=default)=>Task.CompletedTask;public Task StopAsync()=>Task.CompletedTask;
        public Task<ForecastResponse> ForecastAsync(ForecastRequest request,CancellationToken ct=default)=>Task.FromResult(
            Forecast(request.Now,request.Level) with { Quality=new() { State="ok" } });
    }
}
