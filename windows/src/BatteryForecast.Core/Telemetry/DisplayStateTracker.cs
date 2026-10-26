using System.Buffers.Binary;

namespace BatteryForecast.Core.Telemetry;

/// <summary>Session display state reported by Windows. Unknown until a genuine notification arrives.</summary>
public sealed class DisplayStateTracker
{
    public static readonly Guid Setting = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    int state = -1;
    public double? Value => Volatile.Read(ref state) is var s && s >= 0 ? s : null;

    public bool Update(ReadOnlySpan<byte> notification)
    {
        // POWERBROADCAST_SETTING: GUID, DWORD DataLength, DWORD display state.
        if (notification.Length != 24 || new Guid(notification[..16]) != Setting ||
            BinaryPrimitives.ReadUInt32LittleEndian(notification[16..20]) != 4) return false;
        var next = BinaryPrimitives.ReadUInt32LittleEndian(notification[20..24]);
        if (next > 2) return false;
        return Interlocked.Exchange(ref state, (int)next) != next;
    }
}
