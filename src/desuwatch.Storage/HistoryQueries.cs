namespace Desuwatch.Storage;

/// <summary>
/// A single per-minute aggregate point in a history timeseries query.
/// BucketUtc is the start of the UTC minute as Unix seconds — the same
/// convention used throughout <see cref="HistoryStore"/>.
/// </summary>
public readonly record struct HistoryTimeseriesPoint(long BucketUtc, long BytesTotal);

/// <summary>
/// Per-app totals over a range. Returned sorted by TotalBytes descending
/// so callers can take the top N directly without re-sorting.
/// </summary>
public readonly record struct HistoryAppTotal(
	string ProcessName,
	long BytesSent,
	long BytesReceived)
{
	public long TotalBytes => BytesSent + BytesReceived;
}

/// <summary>
/// A single anomaly record as stored. Projected from the <c>anomalies</c>
/// table for UI display. TimestampUtc is Unix seconds.
/// </summary>
public readonly record struct HistoryAnomaly(
	long TimestampUtc,
	string ProcessName,
	string? HostDomain,
	long Bytes,
	double Mean,
	double StdDev);

/// <summary>
/// Process-level first-sighting record from <c>app_registry</c>.
/// FirstSeenUtc is Unix seconds.
/// </summary>
public readonly record struct HistoryFirstSeen(
	string ProcessName,
	long FirstSeenUtc,
	long LastSeenUtc);