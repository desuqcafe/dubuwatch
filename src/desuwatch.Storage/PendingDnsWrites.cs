namespace Desuwatch.Storage;

/// <summary>
/// In-memory accumulator of DNS cache deltas awaiting flush. Keyed by IP
/// so a burst of repeated resolutions for the same address collapses to
/// a single row. Not thread-safe — the store serializes access behind
/// its own lock.
/// </summary>
internal sealed class PendingDnsWrites
{
	private readonly Dictionary<string, PositiveEntry> _positives = new();
	private readonly Dictionary<string, long> _negatives = new();

	public bool IsEmpty => _positives.Count == 0 && _negatives.Count == 0;

	public void AddPositive(string ip, string hostname, long resolvedAtUtc)
	{
		// A fresh positive resolution supersedes any previous negative.
		_negatives.Remove(ip);
		_positives[ip] = new PositiveEntry(hostname, resolvedAtUtc);
	}

	public void AddNegative(string ip, long failedAtUtc)
	{
		// Only record a negative if we don't already have a positive for
		// this IP this flush cycle — a successful resolve wins.
		if (_positives.ContainsKey(ip)) return;
		_negatives[ip] = failedAtUtc;
	}

	public IEnumerable<KeyValuePair<string, PositiveEntry>> DrainPositives()
	{
		foreach (var kv in _positives) yield return kv;
		_positives.Clear();
	}

	public IEnumerable<KeyValuePair<string, long>> DrainNegatives()
	{
		foreach (var kv in _negatives) yield return kv;
		_negatives.Clear();
	}

	internal readonly record struct PositiveEntry(string Hostname, long ResolvedAtUtc);
}