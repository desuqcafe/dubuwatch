namespace Desuwatch.Storage;

/// <summary>
/// Persistence sidecar for a reverse-DNS resolver. Implementations back
/// the resolver's in-memory caches with durable storage so cache state
/// survives app restarts.
///
/// This interface is defined in Desuwatch.Storage because Storage is the
/// natural home for persistence contracts. Desuwatch.Capture takes a
/// dependency on Storage to consume this interface; the concrete
/// DnsCacheStore implementation lives alongside it in Storage.
/// </summary>
public interface IDnsCachePersistence
{
	/// <summary>
	/// Called once by the resolver at startup. Must complete synchronously
	/// or be safe to race against lookups already in flight.
	/// </summary>
	DnsCacheHydration Hydrate(TimeSpan negativeMaxAge);

	/// <summary>
	/// Queued for batched write. Safe to call from any thread. Overwrites
	/// any prior entry for the same IP.
	/// </summary>
	void RecordPositive(string ipAddress, string hostname);

	/// <summary>
	/// Queued for batched write. Safe to call from any thread.
	/// </summary>
	void RecordNegative(string ipAddress);
}

/// <summary>
/// Bulk-loaded cache contents returned from <see cref="IDnsCachePersistence.Hydrate"/>.
/// </summary>
public sealed class DnsCacheHydration
{
	public IReadOnlyDictionary<string, string> Positives { get; }
	public IReadOnlyDictionary<string, DateTime> Negatives { get; }

	public DnsCacheHydration(
		IReadOnlyDictionary<string, string> positives,
		IReadOnlyDictionary<string, DateTime> negatives)
	{
		Positives = positives;
		Negatives = negatives;
	}

	public static DnsCacheHydration Empty { get; } = new(
		new Dictionary<string, string>(),
		new Dictionary<string, DateTime>());
}