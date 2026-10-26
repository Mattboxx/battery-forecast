using System.Buffers.Binary;
using BatteryForecast.Core.Storage;
using BatteryForecast.Core.Telemetry;
using Microsoft.Data.Sqlite;

namespace BatteryForecast.Tests;

public class DisplayStateTests
{
    [Fact]
    public void Only_valid_session_notifications_change_unknown_state()
    {
        var tracker = new DisplayStateTracker();
        Assert.Null(tracker.Value);
        byte[] bytes = new byte[24];
        DisplayStateTracker.Setting.TryWriteBytes(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4);
        foreach (var state in new uint[] { 1, 0, 2 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), state);
            Assert.True(tracker.Update(bytes));
            Assert.Equal((double)state, tracker.Value);
            Assert.False(tracker.Update(bytes));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 3);
        Assert.False(tracker.Update(bytes));
        Assert.Equal(2, tracker.Value);
        bytes[0] ^= 1;
        Assert.False(tracker.Update(bytes));
        Assert.False(tracker.Update(bytes.AsSpan(0, 20)));
    }

    [Fact]
    public void Schema_three_migrates_without_inventing_display_history_and_exports_all_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bf-display-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new HistoryStore(path))
                store.AppendContext(new(DateTimeOffset.FromUnixTimeSeconds(300), CpuPct: 12, Source: "old"));
            // Recreate the actual previous layout, preserving the old sample.
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "ALTER TABLE context_samples DROP COLUMN display_state; PRAGMA user_version=3;";
                cmd.ExecuteNonQuery();
            }
            using (var store = new HistoryStore(path))
            {
                Assert.Equal(1, store.ContextSummary(DateTimeOffset.UnixEpoch).Samples);
                Assert.Null(store.ModelCovariates(DateTimeOffset.UnixEpoch, DateTimeOffset.FromUnixTimeSeconds(1200))["display_state"][0].Value);
                store.AppendContext(new(DateTimeOffset.FromUnixTimeSeconds(600), Source: "new", DisplayState: 1));
                store.AppendContext(new(DateTimeOffset.FromUnixTimeSeconds(660), DisplayState: 0));
                store.AppendContext(new(DateTimeOffset.FromUnixTimeSeconds(900), DisplayState: 2));
                var rows = store.ModelCovariates(DateTimeOffset.UnixEpoch, DateTimeOffset.FromUnixTimeSeconds(1200))["display_state"];
                Assert.Null(rows[1].Value); // mixed bin is not a fictitious half-on state
                Assert.Equal(2, rows[2].Value);
                using var csv = new StringWriter();
                Assert.Equal(4, store.ExportContextCsv(csv));
                var lines = csv.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Contains("display_state,source", lines[0]);
                Assert.EndsWith(",,old", lines[1].Trim());
                Assert.EndsWith(",1,new", lines[2].Trim());
            }
        }
        finally { foreach (var p in new[] { path, path + "-wal", path + "-shm" }) File.Delete(p); }
    }
}
