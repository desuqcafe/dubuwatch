namespace Desuwatch.Storage;

/// <summary>
/// SQL schema constants and migration for the history database.
/// Schema version lives in a single-row `schema_meta` table. Migrations
/// are forward-only and idempotent via `CREATE TABLE IF NOT EXISTS`.
///
/// Identity note: persistence keys on ProcessName (string) rather than a
/// surrogate ID. Two distinct executables sharing a .exe name in different
/// directories will collide, but this is rare enough to accept per the
/// project's deliberate scope cuts.
/// </summary>
internal static class HistorySchema
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Pragmas applied on every connection open. WAL gives us non-blocking
    /// readers during writes; NORMAL sync is the pragmatic middle ground
    /// for a desktop app (the OS decides fsync cadence, we lose at most
    /// the last committed transaction on a hard crash — acceptable for
    /// telemetry data).
    /// </summary>
    public const string ConnectionPragmas = @"
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA temp_store = MEMORY;
    ";

    public const string CreateTablesSql = @"
        CREATE TABLE IF NOT EXISTS schema_meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS app_usage_minute (
            bucket_utc     INTEGER NOT NULL,
            process_name   TEXT    NOT NULL,
            bytes_sent     INTEGER NOT NULL DEFAULT 0,
            bytes_received INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (bucket_utc, process_name)
        );

        CREATE INDEX IF NOT EXISTS idx_app_usage_bucket
            ON app_usage_minute (bucket_utc);

        CREATE TABLE IF NOT EXISTS host_usage_minute (
            bucket_utc     INTEGER NOT NULL,
            process_name   TEXT    NOT NULL,
            host_domain    TEXT    NOT NULL,
            bytes_sent     INTEGER NOT NULL DEFAULT 0,
            bytes_received INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (bucket_utc, process_name, host_domain)
        );

        CREATE INDEX IF NOT EXISTS idx_host_usage_bucket
            ON host_usage_minute (bucket_utc);

        CREATE INDEX IF NOT EXISTS idx_host_usage_process
            ON host_usage_minute (process_name, bucket_utc);

        CREATE TABLE IF NOT EXISTS app_registry (
            process_name   TEXT    PRIMARY KEY,
            first_seen_utc INTEGER NOT NULL,
            last_seen_utc  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS anomalies (
            timestamp_utc  INTEGER NOT NULL,
            process_name   TEXT    NOT NULL,
            host_domain    TEXT,
            bytes          INTEGER NOT NULL,
            mean           REAL    NOT NULL,
            stddev         REAL    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_anomalies_timestamp
            ON anomalies (timestamp_utc);
    ";

    /// <summary>
    /// Deletes rollup and anomaly rows older than the cutoff. Does not
    /// touch <c>app_registry</c> — we keep the per-app first-seen record
    /// indefinitely since it's small and useful context.
    /// </summary>
    public const string PruneSql = @"
        DELETE FROM app_usage_minute  WHERE bucket_utc     < $cutoff;
        DELETE FROM host_usage_minute WHERE bucket_utc     < $cutoff;
        DELETE FROM anomalies         WHERE timestamp_utc  < $cutoff;
    ";
}