using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desuwatch.Capture;
using Desuwatch.App;

namespace Desuwatch.App.ViewModels;

public sealed partial class AppUsageViewModel : ObservableObject
{
    private const int SparklineCapacity = 60;
    private const double SparklineWidth = 80;
    private const double SparklineHeight = 20;

    private readonly SparklineBuffer _buffer;
    private readonly AnomalySettings _anomalySettings;
    private long _sentThisSecond;
    private long _receivedThisSecond;
    private int _ticksSinceActivity = int.MaxValue;
    private long _peakBytesPerSecond;

    public long LastTickSent { get; private set; }
    public long LastTickReceived { get; private set; }

    /// <summary>
    /// True if the app has transmitted bytes within the recent activity
    /// window (10 seconds). Used for the tray tooltip's "apps active"
    /// count to avoid per-second jitter — network traffic is bursty at
    /// the millisecond level, so an instantaneous count strobes between
    /// zero and many every few seconds even during steady use.
    /// </summary>
    public bool IsRecentlyActive => _ticksSinceActivity <= 10;

    public bool LastTickAnomalous { get; private set; }
    public long LastAnomalyBytes { get; private set; }
    public double LastAnomalyMean { get; private set; }
    public double LastAnomalyStdDev { get; private set; }

    [ObservableProperty] private long _bytesSent;
    [ObservableProperty] private long _bytesReceived;
    [ObservableProperty] private Points _sparklinePoints = new();
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isPulsing;
    [ObservableProperty] private bool _isMostActive;
    [ObservableProperty] private long _currentBytesPerSecond;
    [ObservableProperty] private long _peakBytesPerSecondDisplay;
    [ObservableProperty] private string _topDomainsSummary = "";
    [ObservableProperty] private int _connectionCount;

    private readonly Dictionary<string, RemoteHostUsageViewModel> _hostsByKey = new();

    public ObservableCollection<RemoteHostUsageViewModel> Hosts { get; } = new();

    public int ProcessId { get; }
    public string ProcessName { get; }

    public long TotalBytes => BytesSent + BytesReceived;

    public AppUsageViewModel(int processId, string processName, AnomalySettings anomalySettings)
    {
        ProcessId = processId;
        ProcessName = processName;
        _anomalySettings = anomalySettings;
        _buffer = new SparklineBuffer(
            SparklineCapacity,
            anomalySettings.WarmupSamples,
            anomalySettings.AbsoluteFloorBytes,
            anomalySettings.StdDevThreshold);
    }

    public void Record(NetworkEvent e)
    {
        if (e.Direction == TransferDirection.Sent)
        {
            BytesSent += e.Bytes;
            _sentThisSecond += e.Bytes;
        }
        else
        {
            BytesReceived += e.Bytes;
            _receivedThisSecond += e.Bytes;
        }

        OnPropertyChanged(nameof(TotalBytes));

        RouteToHost(e);
    }

    private void RouteToHost(NetworkEvent e)
    {
        if (string.IsNullOrEmpty(e.RemoteAddress)) return;

        // Initial key is either the registrable "Local network" label or
        // the raw IP. DNS resolution may later promote an IP-keyed row to
        // a domain-keyed one; see ApplyHostnameResolution.
        var key = RemoteHost.GetGroupingKey(e.RemoteAddress, hostname: null);

        if (!_hostsByKey.TryGetValue(key, out var host))
        {
            host = new RemoteHostUsageViewModel(key, key, e.RemoteAddress, _anomalySettings);
            _hostsByKey[key] = host;
            Hosts.Add(host);
        }

        host.Record(e);
    }

    /// <summary>
    /// Called by the main view model when DNS reports a hostname for an IP.
    /// Rekeys the matching host row (if any) under its registrable domain,
    /// merging with an existing domain row if one already exists.
    /// </summary>
    public void ApplyHostnameResolution(string ip, string hostname)
    {
        if (!_hostsByKey.TryGetValue(ip, out var ipRow))
            return;

        var domainKey = RemoteHost.ExtractRegistrableDomain(hostname);

        _hostsByKey.Remove(ip);

        if (_hostsByKey.TryGetValue(domainKey, out var existing))
        {
            existing.MergeFrom(ipRow);
            Hosts.Remove(ipRow);
        }
        else
        {
            ipRow.AssignGroupingKey(domainKey, domainKey);
            _hostsByKey[domainKey] = ipRow;
        }
    }

    /// <summary>
    /// Called once per second by the owning view model. Snapshots the
    /// byte counter, pushes it onto the ring buffer, and rebuilds the
    /// polyline points. Assigning a fresh <see cref="Points"/> instance
    /// is required because Avalonia's Polyline does not observe
    /// mutations to an existing Points collection.
    /// </summary>
public void Tick()
    {
        LastTickSent = _sentThisSecond;
        LastTickReceived = _receivedThisSecond;
        _sentThisSecond = 0;
        _receivedThisSecond = 0;

        var tickTotal = LastTickSent + LastTickReceived;

        if (tickTotal > 0)
            _ticksSinceActivity = 0;
        else if (_ticksSinceActivity < int.MaxValue)
            _ticksSinceActivity++;

        _buffer.Push(tickTotal);
        RebuildPoints();

        CurrentBytesPerSecond = tickTotal;
        if (tickTotal > _peakBytesPerSecond)
            _peakBytesPerSecond = tickTotal;
        PeakBytesPerSecondDisplay = _peakBytesPerSecond;

        // Pulse only on statistically notable spikes — "sudden significant
        // change vs this row's own recent baseline." Suppressed during the
        // warm-up period and below an absolute byte floor.
        LastTickAnomalous = _buffer.TryGetAnomaly(out var cur, out var mean, out var std);
        LastAnomalyBytes = cur;
        LastAnomalyMean = mean;
        LastAnomalyStdDev = std;
        IsPulsing = LastTickAnomalous;

        PruneIdleHosts();
        ReorderHosts();
        RefreshHostSummary();
    }

    /// <summary>
    /// Evicts hosts that haven't seen traffic in the idle threshold.
    /// Exempt while this app is expanded — removing hosts the user is
    /// actively looking at would be disruptive. In-memory only; the
    /// DB-backed history is untouched, so the data's still queryable
    /// from the (future) history view.
    /// </summary>
    private void PruneIdleHosts()
    {
        if (IsExpanded) return;

        var cutoff = DateTime.UtcNow - HostIdleThreshold;
        List<RemoteHostUsageViewModel>? victims = null;

        foreach (var host in Hosts)
        {
            if (host.LastSeen < cutoff)
            {
                victims ??= new List<RemoteHostUsageViewModel>();
                victims.Add(host);
            }
        }

        if (victims is null) return;

        foreach (var victim in victims)
        {
            Hosts.Remove(victim);
            _hostsByKey.Remove(victim.GroupingKey);
        }
    }

    private static readonly TimeSpan HostIdleThreshold = TimeSpan.FromHours(2);

    private void ReorderHosts()
    {
        // Tick each host first so its IsPulsing flag is current for this
        // round of UI updates, then mirror the app-level re-sort.
        foreach (var host in Hosts)
            host.Tick();

        for (var i = 0; i < Hosts.Count - 1; i++)
        {
            var bestIdx = i;
            for (var j = i + 1; j < Hosts.Count; j++)
            {
                if (CompareHosts(Hosts[j], Hosts[bestIdx]) < 0)
                    bestIdx = j;
            }

            if (bestIdx != i)
                Hosts.Move(bestIdx, i);
        }
    }
    
    private void RefreshHostSummary()
    {
        ConnectionCount = Hosts.Count;

        // Hosts are sorted by total bytes descending after ReorderHosts.
        // Top 2 names + "+N more" keeps the subtitle compact and stable.
        if (Hosts.Count == 0)
        {
            TopDomainsSummary = "no active connections";
            return;
        }

        var top1 = Hosts[0].DisplayName;
        if (Hosts.Count == 1)
        {
            TopDomainsSummary = top1;
            return;
        }

        var top2 = Hosts[1].DisplayName;
        if (Hosts.Count == 2)
        {
            TopDomainsSummary = $"{top1}, {top2}";
            return;
        }

        TopDomainsSummary = $"{top1}, {top2}, +{Hosts.Count - 2} more";
    }

    private static int CompareHosts(RemoteHostUsageViewModel a, RemoteHostUsageViewModel b)
    {
        var byBytes = b.TotalBytes.CompareTo(a.TotalBytes);
        if (byBytes != 0) return byBytes;
        return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildPoints()
    {
        Span<long> samples = stackalloc long[SparklineCapacity];
        var n = _buffer.CopyTo(samples);
        if (n == 0)
        {
            SparklinePoints = new Points();
            return;
        }

        var max = _buffer.Max();
        var points = new Points();

        // If there's been no traffic at all, render a flat baseline.
        if (max == 0)
        {
            for (var i = 0; i < n; i++)
            {
                var x = (double)i / (SparklineCapacity - 1) * SparklineWidth;
                points.Add(new Point(x, SparklineHeight - 1));
            }
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                var x = (double)i / (SparklineCapacity - 1) * SparklineWidth;
                var normalized = (double)samples[i] / max;
                var y = SparklineHeight - 1 - (normalized * (SparklineHeight - 2));
                points.Add(new Point(x, y));
            }
        }

        SparklinePoints = points;
    }
    
    /// <summary>
    /// Resets session-scoped counters. Called when the user clicks "Clear
    /// session" from the header. The ring buffer is preserved so the
    /// sparkline keeps its visual continuity.
    /// </summary>
    public void ResetSessionCounters()
    {
        BytesSent = 0;
        BytesReceived = 0;
        _peakBytesPerSecond = 0;
        PeakBytesPerSecondDisplay = 0;
        OnPropertyChanged(nameof(TotalBytes));
        foreach (var host in Hosts)
            host.ResetSessionCounters();
    }
    
    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}
