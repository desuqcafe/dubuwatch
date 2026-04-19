using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Desuwatch.Storage;

namespace Desuwatch.Capture;

/// <summary>
/// Asynchronous reverse-DNS resolver with caching. Never blocks the caller;
/// requests are queued and processed on a background task. Results are
/// broadcast via the <see cref="HostResolved"/> event.
///
/// Caches both positive results (IP → hostname, indefinite) and negative
/// results (IPs that failed to resolve, for a cooldown window) so the same
/// unresolved IP isn't retried on every packet.
/// </summary>
public sealed class DnsResolver : IDisposable
{
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, string> _resolved = new();
    private readonly ConcurrentDictionary<string, DateTime> _negativeCache = new();
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly IDnsCachePersistence? _persistence;
    private Task? _worker;

    public event EventHandler<HostResolvedEventArgs>? HostResolved;

    public DnsResolver() : this(null) { }

    public DnsResolver(IDnsCachePersistence? persistence)
    {
        _persistence = persistence;
    }

    public void Start()
    {
        if (_worker is not null) return;
        HydrateFromPersistence();
        _worker = Task.Run(() => WorkerLoop(_cts.Token));
    }

    private void HydrateFromPersistence()
    {
        if (_persistence is null) return;

        // Fire-and-forget — cache starts cold and warms up within a few
        // hundred ms. Any lookups queued in the meantime will just be
        // deduped by the _resolved.ContainsKey check once they're dequeued.
        Task.Run(() =>
        {
            try
            {
                var hydration = _persistence.Hydrate(NegativeCacheDuration);

                foreach (var kv in hydration.Positives)
                    _resolved.TryAdd(kv.Key, kv.Value);

                foreach (var kv in hydration.Negatives)
                    _negativeCache.TryAdd(kv.Key, kv.Value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[desuwatch] DNS hydration failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Tries to get a cached hostname for an IP without issuing a lookup.
    /// Returns null if unknown or cached as unresolvable.
    /// </summary>
    public string? TryGetCached(string ipAddress)
    {
        return _resolved.TryGetValue(ipAddress, out var name) ? name : null;
    }

    /// <summary>
    /// Queues an IP for reverse lookup if it hasn't already been resolved
    /// or recently failed. Returns immediately.
    /// </summary>
    public void RequestLookup(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress)) return;
        if (_resolved.ContainsKey(ipAddress)) return;
        if (RemoteHost.IsPrivateOrLocal(ipAddress)) return;

        if (_negativeCache.TryGetValue(ipAddress, out var failedAt))
        {
            if (DateTime.UtcNow - failedAt < NegativeCacheDuration) return;
            _negativeCache.TryRemove(ipAddress, out _);
        }

        _pending.Enqueue(ipAddress);
        _signal.Release();
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_pending.TryDequeue(out var ip)) continue;

            // Double-check caches in case the IP was queued twice before we got to it.
            if (_resolved.ContainsKey(ip)) continue;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(LookupTimeout);

                var entry = await Dns.GetHostEntryAsync(ip, timeoutCts.Token);
                var hostname = entry.HostName;

                if (!string.IsNullOrEmpty(hostname) && hostname != ip)
                {
                    _resolved[ip] = hostname;
                    _persistence?.RecordPositive(ip, hostname);
                    HostResolved?.Invoke(this, new HostResolvedEventArgs(ip, hostname));
                }
                else
                {
                    _negativeCache[ip] = DateTime.UtcNow;
                    _persistence?.RecordNegative(ip);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[desuwatch] DNS lookup failed for {ip}: {ex.Message}");
                _negativeCache[ip] = DateTime.UtcNow;
                _persistence?.RecordNegative(ip);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Swallow cancellation-related exceptions on shutdown.
        }
        _cts.Dispose();
        _signal.Dispose();
    }
}

public sealed class HostResolvedEventArgs : EventArgs
{
    public string IpAddress { get; }
    public string Hostname { get; }

    public HostResolvedEventArgs(string ipAddress, string hostname)
    {
        IpAddress = ipAddress;
        Hostname = hostname;
    }
}