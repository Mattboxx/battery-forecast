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
    public void ForecastsFromTheDrainLearnOnlyFromEachOtherAndSayHowTheyLearn()
    {
        var data=History();
        var drain=Forecast(data.Now) with { Method="drain" };
        Assert.Equal(0,Learn(data,drain).Windows);                          // level forecasts are not its evidence
        var both=data with { Saved=data.Saved.Select(s=>s with { Response=s.Response with { Method="drain" } }).ToList() };
        Assert.Equal(8,Learn(both,drain).Windows);
        Assert.Equal(0,Learn(both).Windows);
        var report=new CalibrationInfo { Enabled=true,Reason=LocalCalibration.DrainReason };
        Assert.Equal(drain.Curve,LocalCalibration.Apply(drain,report).Curve);   // not warped
        var collecting=Core.Presentation.CalibrationPresenter.Present(true,report,new BlendInfo { Evaluations=2 });
        Assert.Contains("2/"+EstimateBlend.MinimumEvaluations,collecting);
        var ready=Core.Presentation.CalibrationPresenter.Present(true,report,new BlendInfo { Evaluations=7,PowerWeight=.25 });
        Assert.Contains("7",ready);Assert.Contains("75",ready);
    }

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
        var low=History(1.2,key:"profile|abcdef012345:1");var high=History(.9,key:"profile|abcdef012345:2",startDay:3);
        var all=new Data([..low.Saved,..high.Saved],[..low.Actual,..high.Actual],[..low.Breaks,..high.Breaks],[..low.Context,..high.Context],high.Now);
        var first=Learn(all,Forecast(all.Now,key:"profile|abcdef012345:1"));
        Assert.Equal(1.2,first.Factor,8);Assert.Equal("same_apps_and_load",first.Scope);
        Assert.Equal(.9,Learn(all,Forecast(all.Now,key:"profile|abcdef012345:2")).Factor,8);
        // apps never seen before: no exact evidence, so the same CPU load pools both histories
        var unseen=Learn(all,Forecast(all.Now,key:"profile|ffffff012345:1"));
        Assert.True(unseen.Ready);Assert.Equal("same_load",unseen.Scope);Assert.Equal(16,unseen.Windows);
        Assert.InRange(unseen.Factor,.9,1.2);   // pooled evidence lies between the two histories
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
        // other apps after the forecast, same CPU load: not an exact-app comparison, still a same-load one
        var changed=data with { Context=data.Context.Select(c=>c with { WorkloadKey="different" }).ToList() };
        var report=Learn(changed,Forecast(data.Now,key:"app:1"));
        Assert.Equal("same_load",report.Scope);Assert.Equal(8,report.Windows);
    }
    [Fact]
    public void FasterDischargeNearEmptyIsLearnedPerLevelBandAndMovesTheReserveArrivalEarlier()
    {
        // as measured on a real laptop: the forecast is right above 30%, the battery then goes twice as fast
        var saved=new List<ForecastSnapshot>();var actual=new List<BatterySample>();var breaks=new List<DateTimeOffset>();int id=0;
        double Real(double minutes)=>minutes<=60?90-minutes/60*60:Math.Max(0,30-(minutes-60)/60*120);   // 60%/h, then 120%/h below 30%
        for(int session=0;session<4;session++)
        {
            var start=Zero.AddDays(session);breaks.Add(start);
            for(int minute=0;minute<=80;minute++)
                actual.Add(new(start.AddMinutes(minute),ChargeState.Discharging,Real(minute),false,FullChargeMWh:50000,DesignMWh:50000));
            for(int i=0;i<3;i++)
            {
                var origin=start.AddMinutes(i*30);double level=Real(i*30);
                var points=Enumerable.Range(1,288).Select(k=>new CurvePointDto(origin.AddMinutes(k*5).ToUnixTimeSeconds(),
                    Math.Max(0,level-5*k-2),Math.Max(0,level-5*k),Math.Max(0,level-5*k+2))).ToList();   // forecast: 60%/h throughout
                saved.Add(new(++id,origin.AddSeconds(1),"timestral",Forecast(origin,level) with { Curve=points,ReservePct=10 }));
            }
        }
        var now=actual[^1].TimestampUtc.AddMinutes(10);
        var current=Forecast(now,60) with { ReservePct=10,Curve=[..Enumerable.Range(1,288).Select(k=>new CurvePointDto(now.AddMinutes(k*5).ToUnixTimeSeconds(),
            Math.Max(0,58-5*k),Math.Max(0,60-5*k),Math.Max(0,62-5*k)))],Crossing=new() { Status="within_horizon",CentralUtc=now.AddMinutes(50).ToUnixTimeSeconds() } };
        var report=LocalCalibration.Learn(current,saved,actual,breaks,[],now);
        Assert.True(report.Ready);
        double FactorAt(double level)=>report.LevelFactors![Array.FindIndex(report.LevelPoints!,p=>Math.Abs(p-level)<1e-9)];
        Assert.Equal(1,FactorAt(55),2);Assert.Equal(2,FactorAt(25),2);
        // 60% -> 30% at the forecast speed (30 min), then 30% -> 10% twice as fast (10 min instead of 20)
        var corrected=LocalCalibration.Apply(current,report);
        Assert.Equal(40,(corrected.Crossing.CentralUtc!.Value-now.ToUnixTimeSeconds())/60,0);
    }
    [Fact]
    public void AppWorkloadsThatNeverRepeatStillLearnFromTheSameLoad()
    {
        // as on real PCs: the top apps and their CPU bands differ at every forecast
        var data=History(1.15);
        data=data with
        {
            Saved=data.Saved.Select((s,i)=>new ForecastSnapshot(s.Id,s.IssuedAt,s.Source,s.Response with { Conditions=Conditions($"apps-{i}") })).ToList(),
            Context=data.Actual.Select((a,i)=>new CalibrationContext(a.TimestampUtc,2,null,null,$"moment-{i}")).ToList(),
        };
        var report=Learn(data,Forecast(data.Now,key:"apps-now"));
        Assert.True(report.Ready);Assert.Equal("same_load",report.Scope);Assert.Equal(8,report.Windows);Assert.Equal(1.15,report.Factor,8);
    }
    [Fact]
    public void ALoadChangeAfterTheForecastFallsBackToBroaderEvidenceOnly()
    {
        var data=History(1.1,key:"app:1");
        // CPU band 3 after each forecast instead of 2: not the same load, but within one band
        var busier=data with { Context=data.Context.Select(c=>c with { CpuBand=3 }).ToList() };
        var report=Learn(busier,Forecast(data.Now,key:"app:1"));
        Assert.True(report.Ready);Assert.Equal("similar_load",report.Scope);Assert.Equal(1.1,report.Factor,8);
        // without any context readings only the broadest scope can use these windows
        var unknown=Learn(data with { Context=[] },Forecast(data.Now,key:"app:1"));
        Assert.True(unknown.Ready);Assert.Equal("all_use",unknown.Scope);
    }
    [Fact]
    public void LearnedCorrectionIsBounded()
    {
        var report=Learn(History(3.5));
        Assert.True(report.Ready);Assert.Equal(LocalCalibration.MaximumFactor,report.Factor);
        Assert.All(report.LevelFactors!,f=>Assert.InRange(f,LocalCalibration.MinimumFactor,LocalCalibration.MaximumFactor));
        // a saved or hand-made report outside the bounds is clamped when applied
        var slow=LocalCalibration.Apply(Forecast(Zero),new() { Enabled=true,Ready=true,Factor=.2 });
        Assert.Equal(LocalCalibration.MinimumFactor,slow.Calibration!.Factor);
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
