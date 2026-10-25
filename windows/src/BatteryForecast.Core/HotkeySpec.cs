namespace BatteryForecast.Core;

/// <summary>Portable validation; the native registration happens in the WPF app.</summary>
public readonly record struct HotkeySpec(uint Modifiers, uint Key)
{
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().ToUpperInvariant().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        uint mods = 0;
        foreach (var part in parts[..^1])
        {
            uint bit = part switch { "CTRL" => 2, "ALT" => 1, "SHIFT" => 4, _ => 0 };
            if (bit == 0 || (mods & bit) != 0) return false;
            mods |= bit;
        }
        if ((mods & 3) == 0) return false;
        var last = parts[^1];
        uint key;
        if (last.Length == 1 && char.IsAsciiLetterOrDigit(last[0])) key = last[0];
        else if (last.StartsWith('F') && int.TryParse(last.AsSpan(1), out var f) && f is >= 1 and <= 24 && f != 12) key = (uint)(0x70 + f - 1);
        else return false;
        spec = new(mods, key);
        return true;
    }
}
