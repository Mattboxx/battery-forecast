using System.Runtime.InteropServices;
using BatteryForecast.Core;

namespace BatteryForecast.App;

/// <summary>
/// Scorciatoia globale con RegisterHotKey sulla finestra dei messaggi dell'app (WM_HOTKEY = 0x0312).
/// Se la nuova combinazione è già di un'altra app, resta registrata quella precedente.
/// </summary>
sealed class GlobalHotkey(IntPtr window) : IDisposable
{
    public const int Id = 0x4246;
    const uint NoRepeat = 0x4000;   // tenere premuto non ripete l'evento
    bool registered, suspended;

    public HotkeySpec Current { get; private set; }

    /// <summary>True se registrata (o disattivata con una combinazione vuota); false se Windows la rifiuta.</summary>
    public bool TrySet(HotkeySpec next)
    {
        if (next == Current && (registered || next.IsEmpty)) return true;
        var previous = Current;
        Unregister();
        if (!next.IsEmpty && !RegisterHotKey(window, Id, next.Modifiers | NoRepeat, next.Key))
        {
            if (!previous.IsEmpty && !suspended) registered = RegisterHotKey(window, Id, previous.Modifiers | NoRepeat, previous.Key);
            return false;
        }
        Current = next;
        registered = !next.IsEmpty;
        if (suspended) Unregister();       // durante la registrazione dalle Preferenze resta sospesa
        return true;
    }

    /// <summary>
    /// Mentre l'utente preme la nuova combinazione nelle Preferenze: una scorciatoia registrata viene
    /// consumata da Windows prima di arrivare alla finestra, quindi va tolta temporaneamente.
    /// </summary>
    public void Suspend()
    {
        suspended = true;
        Unregister();
    }

    public bool Resume()
    {
        suspended = false;
        if (Current.IsEmpty || registered) return true;
        registered = RegisterHotKey(window, Id, Current.Modifiers | NoRepeat, Current.Key);
        return registered;
    }

    void Unregister()
    {
        if (registered) UnregisterHotKey(window, Id);
        registered = false;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
