namespace Desuwatch.Storage;

/// <summary>
/// SQL schema constants for the DNS cache database. Kept separate from
/// the history schema so the two DBs can evolve independently — a corrupt
/// DNS cache should never be able to take history down with it.
/// </summary>
internal static class DnsCacheSchema
{
	public const int CurrentVersion = 1;

	public const string ConnectionPragmas = @"
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
    ";

	public const string CreateTablesSql = @"
        CREATE TABLE IF NOT EXISTS schema_meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS dns_positive (
            ip              TEXT    PRIMARY KEY,
            hostname        TEXT    NOT NULL,
            resolved_at_utc INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_dns_positive_resolved
            ON dns_positive (resolved_at_utc);

        CREATE TABLE IF NOT EXISTS dns_negative (
            ip            TEXT    PRIMARY KEY,
            failed_at_utc INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_dns_negative_failed
            ON dns_negative (failed_at_utc);
    ";

	/// <summary>
	/// Drops positives older than <c>$positive_cutoff</c> and negatives
	/// older than <c>$negative_cutoff</c>. Run from the store's flush
	/// cadence (at most hourly).
	/// </summary>
	public const string PruneSql = @"
        DELETE FROM dns_positive WHERE resolved_at_utc < $positive_cutoff;
        DELETE FROM dns_negative WHERE failed_at_utc   < $negative_cutoff;
    ";
}