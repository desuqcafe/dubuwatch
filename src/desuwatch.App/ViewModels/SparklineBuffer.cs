namespace Desuwatch.App.ViewModels;

/// <summary>
/// Fixed-capacity ring buffer of recent bytes-per-second samples for a single
/// process. Push one sample per tick; the oldest sample is discarded once the
/// buffer is full.
/// </summary>
internal sealed class SparklineBuffer
{
	private readonly long[] _samples;
	private readonly int _warmupSamples;
	private readonly long _absoluteFloorBytes;
	private readonly double _stdDevThreshold;
	private int _head;
	private int _count;

	public int Capacity { get; }
	public int Count => _count;

	public SparklineBuffer(
		int capacity,
		int warmupSamples = 20,
		long absoluteFloorBytes = 50 * 1024,
		double stdDevThreshold = 2.5)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		_samples = new long[capacity];
		Capacity = capacity;
		_warmupSamples = warmupSamples;
		_absoluteFloorBytes = absoluteFloorBytes;
		_stdDevThreshold = stdDevThreshold;
	}

	public void Push(long sample)
	{
		_samples[_head] = sample;
		_head = (_head + 1) % Capacity;
		if (_count < Capacity) _count++;
	}

	/// <summary>
	/// Copies the samples in oldest-to-newest order into the supplied span.
	/// Returns the number of samples actually written.
	/// </summary>
	public int CopyTo(Span<long> destination)
	{
		if (_count == 0) return 0;
		var n = Math.Min(_count, destination.Length);
		var start = (_head - _count + Capacity) % Capacity;

		for (var i = 0; i < n; i++)
			destination[i] = _samples[(start + i) % Capacity];

		return n;
	}

	public long Max()
	{
		long max = 0;
		for (var i = 0; i < _count; i++)
			if (_samples[i] > max) max = _samples[i];
		return max;
	}

	/// <summary>
	/// Alias for <see cref="Max"/> — reads more naturally at call sites
	/// that render "peak rate over the 60s window."
	/// </summary>
	public long Peak() => Max();

	public double Mean()
	{
		if (_count == 0) return 0;
		long sum = 0;
		for (var i = 0; i < _count; i++)
			sum += _samples[i];
		return (double)sum / _count;
	}

	public double StdDev()
	{
		if (_count < 2) return 0;
		var mean = Mean();
		double sumSquaredDiff = 0;
		for (var i = 0; i < _count; i++)
		{
			var diff = _samples[i] - mean;
			sumSquaredDiff += diff * diff;
		}
		return Math.Sqrt(sumSquaredDiff / _count);
	}

	/// <summary>
	/// Decides whether the most recently pushed sample is a notable spike
	/// relative to this buffer's history. Returns false during the warm-up
	/// period or when the sample is below an absolute floor.
	/// </summary>
	public bool IsCurrentAnomalous()
	{
		return TryGetAnomaly(out _, out _, out _);
	}

	/// <summary>
	/// If the most recent sample is anomalous, returns true and emits the
	/// sample value plus the mean/stddev it was measured against. Useful
	/// for logging anomaly events with enough context to reconstruct the
	/// baseline later.
	/// </summary>
	public bool TryGetAnomaly(out long current, out double mean, out double stdDev)
	{
		current = 0;
		mean = 0;
		stdDev = 0;

		if (_count < _warmupSamples) return false;

		// Most recently pushed sample sits one slot behind _head.
		var newestIdx = (_head - 1 + Capacity) % Capacity;
		current = _samples[newestIdx];

		if (current < _absoluteFloorBytes) return false;

		mean = Mean();
		stdDev = StdDev();
		var threshold = mean + (_stdDevThreshold * stdDev);
		return current > threshold;
	}
}