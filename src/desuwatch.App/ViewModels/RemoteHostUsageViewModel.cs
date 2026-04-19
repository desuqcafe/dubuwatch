using CommunityToolkit.Mvvm.ComponentModel;
using Desuwatch.Capture;
using Desuwatch.App;

namespace Desuwatch.App.ViewModels;

/// <summary>
/// A single host row inside an expanded app. Represents traffic from one
/// process to one registrable domain (e.g. "googlevideo.com") — multiple
/// underlying IPs may roll up into one of these rows.
/// </summary>
public sealed partial class RemoteHostUsageViewModel : ObservableObject
{
    private const int SparklineCapacity = 60;

    private readonly HashSet<string> _ipAddresses = new();
    private readonly SparklineBuffer _buffer;

    private long _sentThisTick;
    private long _receivedThisTick;

    /// <summary>
    /// Totals drained at each Tick(). The main view model reads these to
    /// persist per-minute rollups, then the buffer snapshot in Tick clears
    /// them. Kept separate from BytesSent/BytesReceived (which are session
    /// lifetime totals).
    /// </summary>
    public long LastTickSent { get; private set; }
    public long LastTickReceived { get; private set; }

    public bool LastTickAnomalous { get; private set; }
    public long LastAnomalyBytes { get; private set; }
    public double LastAnomalyMean { get; private set; }
    public double LastAnomalyStdDev { get; private set; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private long _bytesSent;
    [ObservableProperty] private long _bytesReceived;
    [ObservableProperty] private DateTime _lastSeen;
    [ObservableProperty] private bool _isPulsing;
    
    /// <summary>
    /// The grouping key used for lookup in the parent's host dictionary.
    /// Starts as the raw IP, gets promoted to a registrable domain once
    /// DNS resolves.
    /// </summary>
    public string GroupingKey { get; private set; }

    public long TotalBytes => BytesSent + BytesReceived;

    public IReadOnlyCollection<string> IpAddresses => _ipAddresses;

    public RemoteHostUsageViewModel(
        string groupingKey,
        string displayName,
        string initialIp,
        AnomalySettings anomalySettings)
    {
        GroupingKey = groupingKey;
        _displayName = displayName;
        _ipAddresses.Add(initialIp);
        _lastSeen = DateTime.UtcNow;
        _buffer = new SparklineBuffer(
            SparklineCapacity,
            anomalySettings.WarmupSamples,
            anomalySettings.AbsoluteFloorBytes,
            anomalySettings.StdDevThreshold);
    }

    public void Record(NetworkEvent e)
    {
        _ipAddresses.Add(e.RemoteAddress);
        LastSeen = e.Timestamp;

        if (e.Direction == TransferDirection.Sent)
        {
            BytesSent += e.Bytes;
            _sentThisTick += e.Bytes;
        }
        else
        {
            BytesReceived += e.Bytes;
            _receivedThisTick += e.Bytes;
        }

        OnPropertyChanged(nameof(TotalBytes));
    }

    /// <summary>
    /// Called once per second by the parent app row. Pushes the last
    /// tick's byte count onto this host's ring buffer and flips the pulse
    /// flag if the sample is statistically anomalous.
    /// </summary>
    public void Tick()
    {
        LastTickSent = _sentThisTick;
        LastTickReceived = _receivedThisTick;
        _sentThisTick = 0;
        _receivedThisTick = 0;

        _buffer.Push(LastTickSent + LastTickReceived);

        LastTickAnomalous = _buffer.TryGetAnomaly(out var cur, out var mean, out var std);
        LastAnomalyBytes = cur;
        LastAnomalyMean = mean;
        LastAnomalyStdDev = std;
        IsPulsing = LastTickAnomalous;
    }

    /// <summary>
    /// Promotes this row from an IP-keyed row to a domain-keyed row after
    /// DNS resolution. Caller is responsible for re-keying the parent's
    /// dictionary and merging with any existing row under the new key.
    /// </summary>
    public void AssignGroupingKey(string newKey, string newDisplayName)
    {
        GroupingKey = newKey;
        DisplayName = newDisplayName;
    }
    
    /// <summary>
    /// Resets session-scoped byte counters without touching the IP set
    /// or the sparkline buffer.
    /// </summary>
    public void ResetSessionCounters()
    {
        BytesSent = 0;
        BytesReceived = 0;
        OnPropertyChanged(nameof(TotalBytes));
    }

    /// <summary>
    /// Merges another row's counters into this one. Used when DNS resolution
    /// causes two previously-separate IP rows to collapse under one domain.
    /// </summary>
    public void MergeFrom(RemoteHostUsageViewModel other)
    {
        BytesSent += other.BytesSent;
        BytesReceived += other.BytesReceived;
        _sentThisTick += other._sentThisTick;
        _receivedThisTick += other._receivedThisTick;
        if (other.LastSeen > LastSeen) LastSeen = other.LastSeen;
        foreach (var ip in other._ipAddresses)
            _ipAddresses.Add(ip);
        OnPropertyChanged(nameof(TotalBytes));
    }
}