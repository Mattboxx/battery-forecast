using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryForecast.Core;

/// <summary>Light = Alba, Dark = Crepuscolo, Midnight = Notte, Mist = Nebbia; System segue Windows (Alba/Crepuscolo).</summary>
public enum ThemeChoice { System, Light, Dark, Midnight, Mist }
public enum LanguageChoice { System, Italian, English }
public enum TrayText { None, Percent }

/// <summary>
/// Quanto spesso far girare il modello. Il motore si avvia solo per calcolare e si chiude subito dopo:
/// "Balanced" = in background al massimo ogni 30 minuti a batteria, più all'apertura del pannello.
/// </summary>
public enum RefreshPolicy { OnOpen, Balanced, Frequent }

/// <summary>Preferenze dell'utente, in data\settings.json. Valori fuori scala tornano ai predefiniti.</summary>
public sealed record Settings
{
    public double ReservePct { get; init; } = 5;
    /// <summary>Ora locale dell'obiettivo serale ("Alle 22:00 circa …%").</summary>
    public TimeOnly TargetTime { get; init; } = new(22, 0);
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    public LanguageChoice Language { get; init; } = LanguageChoice.System;
    public TrayText TrayText { get; init; } = TrayText.None;
    public RefreshPolicy Refresh { get; init; } = RefreshPolicy.Balanced;
    public bool LaunchAtLogin { get; init; }
    public int RetentionDays { get; init; } = 90;
    public NotificationSettings Notifications { get; init; } = new();
    public bool OnboardingDone { get; init; }
    /// <summary>Controllo giornaliero delle release su GitHub (solo api.github.com).</summary>
    public bool AutoUpdateCheck { get; init; } = true;
    public DateTimeOffset? LastUpdateCheck { get; init; }

    public Settings Normalized() => this with
    {
        ReservePct = ReservePct is >= 1 and <= 30 ? ReservePct : 5,
        RetentionDays = RetentionDays is >= 7 and <= 3650 ? RetentionDays : 90,
        Notifications = Notifications.Normalized(),
    };

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static Settings Load(string path)
    {
        try { return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new()).Normalized(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path) => AppPaths.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
}

public sealed record NotificationSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>Avvisi quando la stima centrale scende sotto questi minuti dalla riserva.</summary>
    public int[] MinutesLeft { get; init; } = [60, 30, 15];
    /// <summary>Avviso a questa percentuale (0 = disattivato).</summary>
    public int LowPct { get; init; } = 10;
    /// <summary>Avviso di carica raggiunta (0 = disattivato).</summary>
    public int ChargedPct { get; init; } = 0;
    public bool UnusualDrain { get; init; } = true;

    public NotificationSettings Normalized() => this with
    {
        MinutesLeft = [.. MinutesLeft.Where(m => m is >= 5 and <= 600).Distinct().OrderDescending()],
        LowPct = LowPct is >= 0 and <= 50 ? LowPct : 10,
        ChargedPct = ChargedPct is 0 or (>= 50 and <= 100) ? ChargedPct : 0,
    };
}
