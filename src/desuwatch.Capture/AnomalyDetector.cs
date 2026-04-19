namespace Desuwatch.Capture;

/// <summary>
/// Decides whether the most recent per-second sample of a time series is
/// statistically unusual compared to its recent history. Used to drive
/// subtle "something notable just happened" UI signals.
/// </summary>
public static class AnomalyDetector
{
	/// <summary>Minimum samples before the detector is willing to fire.</summary>
	public const int WarmupSamples = 20;

	/// <summary>
	/// Absolute floor in bytes. Even a big relative spike over a tiny
	/// baseline is ignored if the raw magnitude is below this. Prevents
	/// trivial handshakes and keepalives from triggering the pulse.
	/// </summary>
	public const long AbsoluteFloorBytes = 50 * 1024;

	/// <summary>Number of standard deviations above the mean to count as anomalous.</summary>
	public const double StdDevThreshold = 2.5;

	/// <summary>
	/// Returns true if <paramref name="currentSample"/> is a notable spike
	/// relative to the supplied <paramref name="recentMean"/> /
	/// <paramref name="recentStdDev"/>, given how many samples the caller
	/// has so far. Callers should compute mean/stddev over samples that
	/// INCLUDE the current one (simpler and matches <see cref="SparklineBuffer"/>'s
	/// existing shape).
	/// </summary>
	public static bool IsAnomalous(
		long currentSample,
		double recentMean,
		double recentStdDev,
		int sampleCount)
	{
		if (sampleCount < WarmupSamples) return false;
		if (currentSample < AbsoluteFloorBytes) return false;

		var threshold = recentMean + (StdDevThreshold * recentStdDev);
		return currentSample > threshold;
	}
}