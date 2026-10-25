using System.Text.Json;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Storage;

namespace BatteryForecast.Core.Notifications;

public sealed record BatteryNotice(string Rule, string TextKey, double Value = 0, string Source = "");
public sealed record NotificationInput(BatterySample Battery, DateTimeOffset? ReserveAt,
    bool EstimateFresh, string Source, IReadOnlyList<BatterySample> RecentSamples, bool Demo = false);

/// <summary>Persisted across restarts. Charge rearming needs a drop of 5 percentage points.</summary>
public sealed class NotificationMemory
{
    public string Session { get; set; } = Guid.NewGuid().ToString("N");
    public bool? OnAc { get; set; }
    public int? Design { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? LastSent { get; set; }
    public HashSet<string> Consumed { get; set; } = [];
    public bool ChargeArmed { get; set; } = true;
    public int ChargeThreshold { get; set; }
}

public sealed class NotificationRules
{
    readonly HistoryStore store;
    readonly TimeProvider clock;
    NotificationMemory memory;

    public NotificationRules(HistoryStore store, TimeProvider? clock = null)
    {
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
        try { memory = JsonSerializer.Deserialize<NotificationMemory>(store.LoadNotificationState() ?? "{}") ?? new(); }
        catch (JsonException) { memory = new(); }
    }

    public void Reset() => memory = new();

    public BatteryNotice? Evaluate(NotificationInput input, NotificationSettings settings)
    {
        var now = clock.GetUtcNow();
        var s = input.Battery;
        if (input.Demo || s.State is ChargeState.Unknown or ChargeState.NotPresent ||
            s.LevelPct is not double level || !double.IsFinite(level) || level is < 0 or > 100 ||
            s.OnAc is not bool ac || now - s.TimestampUtc > TimeSpan.FromMinutes(2) || s.TimestampUtc > now)
            return null;
        // An observed AC sample between evaluations also invalidates the old discharge session.
        var unseenAc = !ac && store.LastAcTimestamp() is { } at && memory.LastSeen is { } seen && at > seen;
        if ((!ac && memory.OnAc == true) || unseenAc ||
            (s.DesignMWh is int d && memory.Design is int previous && d != previous))
        {
            memory.Session = Guid.NewGuid().ToString("N");
            memory.Consumed.Clear();
        }
        memory.OnAc = ac;
        memory.Design = s.DesignMWh ?? memory.Design;
        memory.LastSeen = s.TimestampUtc;
        if (memory.ChargeThreshold != settings.ChargedPct)
        { memory.ChargeThreshold = settings.ChargedPct; memory.ChargeArmed = true; }
        if (level <= settings.ChargedPct - 5) memory.ChargeArmed = true;

        var candidates = new List<BatteryNotice>();
        var breachedTime = new List<string>();
        if (ac)
        {
            if (settings.ChargedPct > 0 && level >= settings.ChargedPct && memory.ChargeArmed)
                candidates.Add(new("charged", "NoticeCharged", level));
        }
        else
        {
            if (settings.LowPct > 0 && level <= settings.LowPct && !memory.Consumed.Contains("low"))
                candidates.Add(new("low", "NoticeLow", level));
            if (input.EstimateFresh && input.ReserveAt is { } reserve)
            {
                var minutes = Math.Max(0, (reserve - now).TotalMinutes);
                foreach (var threshold in settings.MinutesLeft.Order())
                {
                    var key = $"minutes:{threshold}";
                    if (minutes <= threshold) breachedTime.Add(key);
                }
                var mostUrgent = breachedTime.FirstOrDefault();
                if (mostUrgent is not null && !memory.Consumed.Contains(mostUrgent))
                    candidates.Add(new(mostUrgent, "NoticeMinutes", Math.Max(0, Math.Round(minutes / 5) * 5), input.Source));
            }
            if (settings.UnusualDrain && !memory.Consumed.Contains("drain") && SustainedDrain(input.RecentSamples))
                candidates.Add(new("drain", "NoticeDrain"));
        }
        BatteryNotice? notice = null;
        if (settings.Enabled && (memory.LastSent is null || now - memory.LastSent >= TimeSpan.FromMinutes(5)))
            notice = candidates.FirstOrDefault();
        if (notice is not null)
        {
            memory.LastSent = now;
            memory.Consumed.Add(notice.Rule);
            if (notice.Rule == "charged") memory.ChargeArmed = false;
            // A jump through several thresholds has one message, never deferred less-urgent alerts.
            foreach (var rule in breachedTime) memory.Consumed.Add(rule);
        }
        store.SaveNotificationState(JsonSerializer.Serialize(memory), now, memory.Session, notice?.Rule);
        return notice;
    }

    public static bool SustainedDrain(IReadOnlyList<BatterySample> samples)
    {
        if (samples.Count < 26) return false;
        var end = samples[^1].TimestampUtc;
        var tail = samples.Where(s => s.TimestampUtc >= end.AddMinutes(-30)).ToArray();
        if (tail.Length < 26 || end - tail[0].TimestampUtc < TimeSpan.FromMinutes(28)) return false;
        for (int i = 0; i < tail.Length; i++)
        {
            if (!tail[i].IsDischarging || tail[i].State == ChargeState.Unknown || tail[i].DischargePowerMW is not > 0) return false;
            if (i > 0 && (tail[i].TimestampUtc - tail[i-1].TimestampUtc > TimeSpan.FromMinutes(2) ||
                tail[i].TimestampUtc <= tail[i-1].TimestampUtc || tail[i].LevelPct > tail[i-1].LevelPct + 1.5 ||
                tail[i].DesignMWh != tail[i-1].DesignMWh)) return false;
        }
        var baseline = tail.Where(s => s.TimestampUtc < end.AddMinutes(-10)).Select(s => s.DischargePowerMW!.Value).Order().ToArray();
        var recent = tail.Where(s => s.TimestampUtc >= end.AddMinutes(-10)).Select(s => s.DischargePowerMW!.Value).Order().ToArray();
        if (baseline.Length < 16 || recent.Length < 9) return false;
        var reference = baseline[baseline.Length / 2];
        var current = recent[recent.Length / 2];
        return current >= 5000 && current >= reference * 2.0 && current - reference >= 3000;
    }
}
