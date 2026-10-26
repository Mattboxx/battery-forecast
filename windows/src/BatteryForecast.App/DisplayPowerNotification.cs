using System.Runtime.InteropServices;
using BatteryForecast.Core.Telemetry;

namespace BatteryForecast.App;

/// <summary>Interactive-session state; no polling, screen capture, idle-time inference or power changes.</summary>
sealed class DisplayPowerNotification : IDisposable
{
    readonly DisplayStateTracker tracker;
    readonly Action<int> changed;
    IntPtr registration;
    public bool IsRegistered => registration != IntPtr.Zero;
    public int RegistrationError { get; private set; }
    public DisplayPowerNotification(DisplayStateTracker tracker, Action<int> changed)
    {
        this.tracker = tracker;
        this.changed = changed;
    }
    public void Start(IntPtr window)
    {
        if (registration != IntPtr.Zero) return;
        var setting = DisplayStateTracker.Setting;
        registration = RegisterPowerSettingNotification(window, ref setting, 0);
        if (registration == IntPtr.Zero) RegistrationError = Marshal.GetLastWin32Error();
    }
    public void OnMessage(IntPtr data)
    {
        if (data == IntPtr.Zero || Marshal.ReadInt32(data, 16) != 4) return;
        byte[] bytes = new byte[24];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        if (tracker.Update(bytes)) changed((int)tracker.Value!.Value);
    }
    public void Dispose()
    {
        if (registration != IntPtr.Zero) UnregisterPowerSettingNotification(registration);
    }
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterPowerSettingNotification(IntPtr registration);
}
