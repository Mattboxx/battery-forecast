namespace BatteryForecast.Tests;

internal static class LegacySchema
{
    // Existing migration fixtures start from a current DB; actually restore the old layout first.
    internal const string DropTemporalMetadata = """
        ALTER TABLE context_samples DROP COLUMN cpu_start;
        ALTER TABLE context_samples DROP COLUMN measured_at;
        ALTER TABLE context_samples DROP COLUMN collection_kind;
        ALTER TABLE context_samples DROP COLUMN optional_observed_at;
        ALTER TABLE battery_samples DROP COLUMN measured_at;
        ALTER TABLE battery_samples DROP COLUMN collection_kind;
        """;
}
