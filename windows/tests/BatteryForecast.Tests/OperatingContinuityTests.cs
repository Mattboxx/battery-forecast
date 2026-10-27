using BatteryForecast.Core.Telemetry;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core;
using BatteryForecast.Core.Forecasting;
using Microsoft.Extensions.Time.Testing;

namespace BatteryForecast.Tests;

public class OperatingContinuityTests
{
    [Fact]
    public void SleepAndHibernateAreDetectedEvenWithoutNotifications()
    {
        var tracker=new SamplingContinuity();var zero=DateTimeOffset.UtcNow;
        Assert.Null(tracker.Observe(zero,new(100,100)));
        Assert.Null(tracker.Observe(zero.AddSeconds(60),new(160,160)));
        var edge=tracker.Observe(zero.AddHours(8),new(28900,170));
        Assert.Equal("resume_detected",edge!.Value.Kind);
        Assert.Equal(zero.AddSeconds(60),edge.Value.From);
    }

    [Fact]
    public void WallClockJumpAndUnobservedGapAreDistinctFromSleep()
    {
        var zero=DateTimeOffset.UtcNow;var tracker=new SamplingContinuity();
        tracker.Observe(zero,new(100,100));
        Assert.Equal("clock_changed",tracker.Observe(zero.AddHours(1),new(160,160))!.Value.Kind);
        tracker.Reset();tracker.Observe(zero,new(100,100));
        Assert.Equal("sampling_gap",tracker.Observe(zero.AddHours(1),new(3700,3700))!.Value.Kind);
    }

    [Fact]
    public void NativeWorkingClockIsAvailableWithoutChangingPowerSettings()
    {
        var value=OperatingClock.Read();
        Assert.NotNull(value);Assert.True(value.Value.ElapsedSeconds>=value.Value.AwakeSeconds-1);
    }

    [Fact]
    public void SuspendStopsPersistenceAndInvalidatesForecastEvenInOnOpenMode()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-suspend-{Guid.NewGuid():N}.db");
        var clock=new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            using var store=new HistoryStore(path);var engine=new FakeEngine();
            using var collector=new Collector(new FakeBattery(clock),store,clock);
            using var coordinator=new ForecastCoordinator(collector,store,engine,()=>new Settings { Refresh=RefreshPolicy.OnOpen },clock:clock);
            coordinator.Start();coordinator.RecalculateNow();
            Assert.True(SpinWait.SpinUntil(()=>coordinator.State?.Forecast is not null,TimeSpan.FromSeconds(5)));
            collector.OnSuspend();Assert.Null(coordinator.State!.Forecast);
            var before=store.Samples(DateTimeOffset.MinValue).Count;
            clock.Advance(TimeSpan.FromMinutes(1));collector.Sample();
            Assert.Equal(before,store.Samples(DateTimeOffset.MinValue).Count);
            Assert.True(collector.OnResume());Assert.False(collector.OnResume());
            Assert.Null(coordinator.State!.Forecast); // OnOpen respects explicit recalculation after wake
        }
        finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"}) File.Delete(f); }
    }

    [Fact]
    public void NativeClockRecoversWhenTheResumeNotificationIsMissing()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-missed-resume-{Guid.NewGuid():N}.db");
        var clock=new FakeTimeProvider(DateTimeOffset.UtcNow);OperatingClockReading os=new(100,100);
        try
        {
            using var store=new HistoryStore(path);
            using var collector=new Collector(new FakeBattery(clock),store,clock,operatingClock:()=>os);
            collector.Sample();collector.OnSuspend();
            clock.Advance(TimeSpan.FromHours(8));os=new(28900,110);
            collector.Sample();
            Assert.False(collector.IsSuspended);
            Assert.Equal(2,store.Samples(DateTimeOffset.MinValue).Count);
            Assert.False(collector.OnResume()); // later OS notification refers to the same wake
            Assert.Contains(store.ModelDiscontinuities(DateTimeOffset.MinValue,clock.GetUtcNow()),t=>t.ToUnixTimeSeconds()==clock.GetUtcNow().ToUnixTimeSeconds());
        }
        finally { foreach(var f in new[]{path,path+"-wal",path+"-shm"}) File.Delete(f); }
    }

    [Fact]
    public async Task PreSleepReplyCannotReappearOrOverwriteCacheAfterWake()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-late-resume-{Guid.NewGuid():N}.db");
        var cache=path+".json";var clock=new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            using var store=new HistoryStore(path);var engine=new DelayedEngine();
            using var collector=new Collector(new FakeBattery(clock),store,clock);
            using var coordinator=new ForecastCoordinator(collector,store,engine,()=>new Settings { Refresh=RefreshPolicy.OnOpen },cache,clock);
            coordinator.Start();coordinator.RecalculateNow();
            var request=await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            collector.OnSuspend();clock.Advance(TimeSpan.FromMinutes(1));collector.OnResume();
            engine.Finished.SetResult(new ForecastResponse
            {
                ContextEndUtc=request.Now.ToUnixTimeSeconds(),GeneratedAtUtc=request.Now.ToUnixTimeSeconds(),Level=60,ReservePct=5,
                Source="test",Quality=new QualityDto { State="ok" },Curve=[new(request.Now.AddMinutes(5).ToUnixTimeSeconds(),50,55,60)]
            });
            Assert.True(SpinWait.SpinUntil(()=>coordinator.State?.Updating==false,TimeSpan.FromSeconds(5)));
            Assert.Null(coordinator.State!.Forecast);Assert.False(File.Exists(cache));
            Assert.Empty(store.ForecastSnapshots(DateTimeOffset.MinValue,DateTimeOffset.MaxValue));
        }
        finally { foreach(var f in new[]{path,path+"-wal",path+"-shm",cache}) File.Delete(f); }
    }

    sealed class DelayedEngine : IForecastEngine
    {
        internal readonly TaskCompletionSource<ForecastRequest> Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ForecastResponse> Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerReady? Info=>new("test","test",0); public bool IsWarm=>true;
        public Task WarmUpAsync(CancellationToken ct=default)=>Task.CompletedTask;
        public Task StopAsync()=>Task.CompletedTask;
        public Task<ForecastResponse> ForecastAsync(ForecastRequest request,CancellationToken ct=default) { Started.TrySetResult(request);return Finished.Task; }
    }
}
