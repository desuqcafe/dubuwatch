namespace Desuwatch.Storage;

/// <summary>
/// A single anomaly event flagged by the sparkline buffer's statistical
/// detector. Host-level anomalies set <see cref="HostDomain"/>; app-level
/// anomalies leave it null.
/// </summary>
public readonly record struct AnomalyRecord(
	DateTime TimestampUtc,
	string ProcessName,
	string? HostDomain,
	long Bytes,
	double Mean,
	double StdDev);