namespace BatteryForecast.Core;

/// <summary>
/// Scorciatoia globale: modificatori (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8) più un tasto virtuale.
/// Validazione e testo canonico ("Ctrl+Alt+B", "Win+Shift+F9") sono qui; la registrazione è nell'app WPF.
/// Serve almeno Ctrl, Alt o Win: con il solo Shift si intercetterebbero le maiuscole digitate.
/// </summary>
public readonly record struct HotkeySpec(uint Modifiers, uint Key)
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public bool IsEmpty => Key == 0;

    // nomi stabili per i tasti non alfanumerici (gli stessi nel testo salvato e mostrato)
    static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
    };

    /// <summary>Testo vuoto = scorciatoia disattivata (valida, chiave 0).</summary>
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        uint mods = 0;
        foreach (var part in parts[..^1])
        {
            uint bit = part.ToUpperInvariant() switch { "CTRL" or "CONTROL" => Ctrl, "ALT" => Alt, "SHIFT" => Shift, "WIN" or "WINDOWS" => Win, _ => 0 };
            if (bit == 0 || (mods & bit) != 0) return false;
            mods |= bit;
        }
        return KeyFromName(parts[^1], out var key) && TryCreate(mods, key, out spec);
    }

    /// <summary>Da modificatori e tasto virtuale premuti (registrazione dalla tastiera).</summary>
    public static bool TryCreate(uint modifiers, uint virtualKey, out HotkeySpec spec)
    {
        spec = default;
        if ((modifiers & (Ctrl | Alt | Win)) == 0 || (modifiers & ~0xFu) != 0) return false;
        if (NameOf(virtualKey) is null) return false;
        spec = new(modifiers, virtualKey);
        return true;
    }

    static bool KeyFromName(string name, out uint key)
    {
        key = 0;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) { key = char.ToUpperInvariant(name[0]); return true; }
        if (Named.TryGetValue(name, out key)) return true;
        if (name.Length >= 2 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24 && f != 12)
        { key = (uint)(0x70 + f - 1); return true; }        // F12 è riservato al debugger da Windows
        return false;
    }

    static string? NameOf(uint vk) => vk switch
    {
        >= '0' and <= '9' or >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 when vk != 0x7B => $"F{vk - 0x70 + 1}",
        _ => Named.FirstOrDefault(kv => kv.Value == vk).Key,
    };

    /// <summary>Testo canonico, nell'ordine di Windows: Ctrl+Alt+Shift+Win+Tasto. Vuoto se disattivata.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(NameOf(Key) ?? $"0x{Key:X2}");
        return string.Join('+', parts);
    }
}
