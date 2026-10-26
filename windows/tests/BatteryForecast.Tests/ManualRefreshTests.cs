using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;
using BatteryForecast.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace BatteryForecast.Tests;

public class ManualRefreshTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repeated_manual_clicks_do_not_queue_models_and_sensor_opt_out_reaches_engine(bool useContext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bf-manual-{Guid.NewGuid():N}.db");
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1800000000));
        try
        {
            using var store = new HistoryStore(path);
            store.AppendContext(new(clock.GetUtcNow().AddMinutes(-5), CpuPct: 37));
            using var collector = new Collector(new Battery(clock), store, clock);
            var engine = new WaitingEngine();
            using var coordinator = new ForecastCoordinator(collector, store, engine,
                () => new Settings { Refresh = RefreshPolicy.OnOpen, UseSystemContext = useContext }, clock: clock);
            coordinator.Start();
            coordinator.RecalculateNow();
            var request = await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 5; i++) coordinator.RecalculateNow();
            await Task.Delay(100);
            Assert.Equal(1, engine.Calls);
            if (useContext) Assert.Equal(37, Assert.Single(request.PastCovariates!["cpu_pct"]).Value);
            else Assert.Null(request.PastCovariates);
            engine.Finished.SetResult(new ForecastResponse
            {
                ContextEndUtc = request.Now.ToUnixTimeSeconds(), GeneratedAtUtc = request.Now.ToUnixTimeSeconds(),
                Level = request.Level, ReservePct = request.ReservePct, Source = "test",
                Curve = [new(request.Now.AddMinutes(5).ToUnixTimeSeconds(), 50, 55, 60)],
            });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (coordinator.State?.Updating == true && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.False(coordinator.State!.Updating);
            await Task.Delay(100);
            Assert.Equal(1, engine.Calls);
        }
        finally { foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file); }
    }
    sealed class Battery(FakeTimeProvider clock) : IBatteryProvider
    { public BatterySample Read() => new(clock.GetUtcNow(), ChargeState.Discharging, 60, false, BatterySaver: true); }
    sealed class WaitingEngine : IForecastEngine
    {
        public int Calls;
        public TaskCompletionSource<ForecastRequest> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ForecastResponse> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsWarm => true;
        public WorkerReady? Info => new("test", "test", 0);
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<ForecastResponse> ForecastAsync(ForecastRequest r, CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(r); return Finished.Task; }
    }
}
