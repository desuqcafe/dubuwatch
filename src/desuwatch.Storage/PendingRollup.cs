namespace Desuwatch.Storage;

/// <summary>
/// In-memory accumulator of deltas awaiting flush to the history database.
/// Keyed by <c>(bucket_utc, process_name)</c> for app rollups and
/// <c>(bucket_utc, process_name, host_domain)</c> for host rollups.
///
/// Not thread-safe. The store owns a single instance and all access is
/// serialized behind the store's lock.
/// </summary>
internal sealed class PendingRollup
{
    private readonly Dictionary<AppKey, Totals> _apps = new();
    private readonly Dictionary<HostKey, Totals> _hosts = new();
    private readonly Dictionary<string, (long firstSeen, long lastSeen)> _registry = new();
    private readonly List<AnomalyRecord> _anomalies = new();

    public int AppCount => _apps.Count;
    public int HostCount => _hosts.Count;
    public int AnomalyCount => _anomalies.Count;
    public bool IsEmpty =>
        _apps.Count == 0 && _hosts.Count == 0 && _registry.Count == 0 && _anomalies.Count == 0;

    public void AddAppDelta(long bucketUtc, string processName, long sent, long received)
    {
        var key = new AppKey(bucketUtc, processName);
        if (_apps.TryGetValue(key, out var existing))
            _apps[key] = new Totals(existing.Sent + sent, existing.Received + received);
        else
            _apps[key] = new Totals(sent, received);
    }

    public void AddHostDelta(long bucketUtc, string processName, string hostDomain, long sent, long received)
    {
        var key = new HostKey(bucketUtc, processName, hostDomain);
        if (_hosts.TryGetValue(key, out var existing))
            _hosts[key] = new Totals(existing.Sent + sent, existing.Received + received);
        else
            _hosts[key] = new Totals(sent, received);
    }

    public void TouchRegistry(string processName, long nowUtc)
    {
        if (_registry.TryGetValue(processName, out var existing))
            _registry[processName] = (existing.firstSeen, nowUtc);
        else
            _registry[processName] = (nowUtc, nowUtc);
    }

    public void AddAnomaly(AnomalyRecord record) => _anomalies.Add(record);

    public IEnumerable<KeyValuePair<AppKey, Totals>> DrainApps()
    {
        foreach (var kv in _apps) yield return kv;
        _apps.Clear();
    }

    public IEnumerable<KeyValuePair<HostKey, Totals>> DrainHosts()
    {
        foreach (var kv in _hosts) yield return kv;
        _hosts.Clear();
    }

    public IEnumerable<KeyValuePair<string, (long firstSeen, long lastSeen)>> DrainRegistry()
    {
        foreach (var kv in _registry) yield return kv;
        _registry.Clear();
    }

    public IEnumerable<AnomalyRecord> DrainAnomalies()
    {
        foreach (var a in _anomalies) yield return a;
        _anomalies.Clear();
    }

    internal readonly record struct AppKey(long BucketUtc, string ProcessName);
    internal readonly record struct HostKey(long BucketUtc, string ProcessName, string HostDomain);
    internal readonly record struct Totals(long Sent, long Received);
}