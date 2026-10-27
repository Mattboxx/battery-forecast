namespace BatteryForecast.Tests;

internal static class LegacySchema
{
    // Existing migration fixtures start from a current DB; actually restore the old layout first.
    internal const string DropProcessMetadata = """
        ALTER TABLE context_samples DROP COLUMN process_cpu_pct;
        ALTER TABLE context_samples DROP COLUMN process_memory_mb;
        ALTER TABLE context_samples DROP COLUMN workload_profile;
        ALTER TABLE context_samples DROP COLUMN processes_json;
        ALTER TABLE context_samples DROP COLUMN process_start;
        """;
    internal const string DropTemporalMetadata = DropProcessMetadata+"""
        ALTER TABLE context_samples DROP COLUMN cpu_start;
        ALTER TABLE context_samples DROP COLUMN measured_at;
        ALTER TABLE context_samples DROP COLUMN collection_kind;
        ALTER TABLE context_samples DROP COLUMN optional_observed_at;
        ALTER TABLE battery_samples DROP COLUMN measured_at;
        ALTER TABLE battery_samples DROP COLUMN collection_kind;
        """;
}
