using System.Runtime.InteropServices;
using BatteryForecast.Core;

namespace BatteryForecast.App;

sealed class GlobalHotkey(IntPtr window) : IDisposable
{
    public const int Id = 0x4246;
    HotkeySpec current;

    public bool TrySet(string text)
    {
        if (!HotkeySpec.TryParse(text, out var next)) return false;
        if (next == current) return true;
        var previous = current;
        if (previous.Key != 0) UnregisterHotKey(window, Id);
        if (next.Key != 0 && !RegisterHotKey(window, Id, next.Modifiers | 0x4000, next.Key))
        {
            if (previous.Key != 0) RegisterHotKey(window, Id, previous.Modifiers | 0x4000, previous.Key);
            return false;
        }
        current = next;
        return true;
    }

    public void Dispose() { if (current.Key != 0) UnregisterHotKey(window, Id); }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
