namespace BatteryForecast.Core.Battery;

public enum ChargeState { Unknown, NotPresent, Discharging, Idle, Charging }

/// <summary>
/// Una lettura della batteria. I valori che il dispositivo non espone restano null: mai 0, mai
/// le sentinelle delle API (255, -1). <see cref="DischargePowerMW"/> positivo = consumo.
/// </summary>
public sealed record BatterySample(
    DateTimeOffset TimestampUtc,
    ChargeState State,
    double? LevelPct,
    bool? OnAc,
    int? RemainingMWh = null,
    int? FullChargeMWh = null,
    int? DesignMWh = null,
    int? DischargePowerMW = null,
    int? OsRemainingSeconds = null,
    bool? BatterySaver = null,
    string Source = "windows")
{
    public int? ChargingPowerMW { get; init; }

    public bool HasBattery => State != ChargeState.NotPresent;

    /// <summary>A batteria con una percentuale valida: l'unico caso in cui ha senso una previsione.</summary>
    public bool IsDischarging => HasBattery && OnAc == false && LevelPct is not null;

    /// <summary>Salute: capacità a piena carica su capacità di progetto, se entrambe note e plausibili.</summary>
    public double? HealthPct => FullChargeMWh is > 0 && DesignMWh is > 0
        ? 100.0 * FullChargeMWh.Value / DesignMWh.Value
        : null;
}
