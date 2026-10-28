using System.Text;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.Tests;

public class StorageLifetimeTests
{
    [Fact]
    public async Task Closing_waits_for_an_active_database_reader_to_finish()
    {
        var path=Path.Combine(Path.GetTempPath(),$"bf-lifetime-{Guid.NewGuid():N}.db");
        using var writer=new PausedWriter();
        using var closingStarted=new ManualResetEventSlim();
        using var store=new HistoryStore(path);
        var now=DateTimeOffset.UtcNow;
        store.AppendContext(new SystemSample(now,CpuPct:10));
        store.AppendContext(new SystemSample(now.AddMinutes(1),CpuPct:20));
        Task<int>? exporting=null;Task? closing=null;
        try
        {
            exporting=Task.Run(()=>store.ExportContextCsv(writer));
            Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(5)));
            closing=Task.Factory.StartNew(()=> { closingStarted.Set();store.Dispose(); },
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Assert.True(closingStarted.Wait(TimeSpan.FromSeconds(5)));
            bool closedBeforeReaderFinished=await Task.WhenAny(closing,Task.Delay(150))==closing;
            writer.Continue.Set();
            int rows=0;
            var exportError=await Record.ExceptionAsync(async()=>rows=await exporting.WaitAsync(TimeSpan.FromSeconds(5)));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closedBeforeReaderFinished,"The connection closed while an export still held its active reader.");
            Assert.Null(exportError);
            Assert.Equal(2,rows);
        }
        finally
        {
            writer.Continue.Set();
            foreach(var pending in new Task?[]{exporting,closing})
                if(pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch(Exception) { }
            store.Dispose();
            foreach(var file in new[]{path,path+"-wal",path+"-shm"}) File.Delete(file);
        }
    }

    sealed class PausedWriter:TextWriter
    {
        public readonly ManualResetEventSlim Entered=new(),Continue=new();
        public override Encoding Encoding=>Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            Entered.Set();
            if(!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Export reader was not released");
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { Continue.Set();Entered.Dispose();Continue.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
