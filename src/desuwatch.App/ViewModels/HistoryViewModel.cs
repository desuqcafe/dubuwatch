using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desuwatch.App;
using Desuwatch.Storage;

namespace Desuwatch.App.ViewModels;

/// <summary>
/// Owns the history panel's state: current range selection, the queried
/// data for that range, and the export commands. Re-queries on every
/// range change. A single shared <see cref="HistoryStore"/> serves both
/// this VM and the main VM's period totals.
/// <para>
/// File-save interactions are delegated via <see cref="PickSavePathAsync"/>
/// so this class stays view-agnostic and testable. The main window
/// wires it up to Avalonia's storage provider on construction.
/// </para>
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    public delegate Task<string?> SavePathPicker(string suggestedName, string extension);

    private readonly HistoryStore _store;
    private readonly DesuwatchSettings _settings;
    private SavePathPicker? _pickSavePath;

    // Axis span — refreshed every time the range is resolved. Drives the
    // x-coordinate computation in the timeseries builder, so we cache
    // them together rather than recomputing in multiple places.
    private DateTime _rangeFromUtc;
    private DateTime _rangeToUtc;

    // Snapshot of the raw query results. Re-used by export so we don't
    // re-query when the user exports the same range they're viewing.
    private IReadOnlyList<HistoryTimeseriesPoint> _lastTimeseries = Array.Empty<HistoryTimeseriesPoint>();
    private IReadOnlyList<HistoryAppTotal> _lastTopApps = Array.Empty<HistoryAppTotal>();
    private IReadOnlyList<HistoryAnomaly> _lastAnomalies = Array.Empty<HistoryAnomaly>();
    private IReadOnlyList<HistoryFirstSeen> _lastFirstSeen = Array.Empty<HistoryFirstSeen>();

    [ObservableProperty] private HistoryRange _selectedRange = HistoryRange.ThisCycle;
    [ObservableProperty] private string _rangeLabel = "";
    [ObservableProperty] private long _rangeTotalBytes;
    [ObservableProperty] private int _rangeAppCount;
    [ObservableProperty] private int _rangeAnomalyCount;
    [ObservableProperty] private Points _timeseriesLinePoints = new();
    [ObservableProperty] private Points _timeseriesFillPoints = new();
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _emptyStateText = "No activity in this range yet.";
    [ObservableProperty] private bool _isExportMenuOpen;

    // Chart axis / peak display
    [ObservableProperty] private string _peakLabel = "";
    [ObservableProperty] private IReadOnlyList<ChartAxisLabel> _axisLabels =
        Array.Empty<ChartAxisLabel>();
    [ObservableProperty] private IReadOnlyList<ChartAnomalyMarker> _anomalyMarkers =
        Array.Empty<ChartAnomalyMarker>();

    // Hover overlay state — driven by the view's pointer events.
    [ObservableProperty] private bool _isHoverVisible;
    [ObservableProperty] private double _hoverX;
    [ObservableProperty] private double _hoverLabelX;
    [ObservableProperty] private string _hoverTimeText = "";
    [ObservableProperty] private string _hoverValueText = "";
    [ObservableProperty] private string _hoverEventsText = "";
    [ObservableProperty] private bool _hoverHasEvents;

    // Cached so hover can find the nearest sample without re-running
    // the full query — set by RebuildChart.
    private long _chartFirstBucketUtc;
    private long _chartLastBucketUtc;

    public ObservableCollection<HistoryAppRow> TopApps { get; } = new();
    public ObservableCollection<HistoryAnomalyRow> Anomalies { get; } = new();
    public ObservableCollection<HistoryFirstSeenRow> FirstSeen { get; } = new();

    // Chart dimensions — consumed by the view via the timeseries points
    // builder. Exposed here so layout tweaks stay in one place.
    public double TimeseriesWidth { get; set; } = 640;
    public double TimeseriesHeight { get; set; } = 140;

    public HistoryViewModel(HistoryStore store, DesuwatchSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    /// <summary>
    /// Wired by the view after construction so we can prompt for save
    /// locations without dragging Avalonia types into the VM.
    /// </summary>
    public void ConfigureSavePicker(SavePathPicker picker)
    {
        _pickSavePath = picker;
    }

    /// <summary>
    /// Called whenever the panel opens or the range changes. Runs all
    /// four queries against the shared store and rebuilds the
    /// observable collections.
    /// </summary>
    public void RefreshForCurrentRange()
    {
        var now = DateTime.UtcNow;
        var window = HistoryRangeResolver.Resolve(
            SelectedRange, now, _settings.BillingCycleResetDayClamped);

        _rangeFromUtc = window.FromUtc;
        _rangeToUtc = window.ToUtc;
        RangeLabel = window.Label;

        try
        {
            _lastTimeseries = _store.GetTimeseries(window.FromUtc, window.ToUtc);
            _lastTopApps = _store.GetTopApps(window.FromUtc, window.ToUtc, limit: 50);
            _lastAnomalies = _store.GetAnomalies(window.FromUtc, window.ToUtc, limit: 100);
            _lastFirstSeen = _store.GetFirstSeenInRange(window.FromUtc, window.ToUtc, limit: 20);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] history query failed: {ex}");
            _lastTimeseries = Array.Empty<HistoryTimeseriesPoint>();
            _lastTopApps = Array.Empty<HistoryAppTotal>();
            _lastAnomalies = Array.Empty<HistoryAnomaly>();
            _lastFirstSeen = Array.Empty<HistoryFirstSeen>();
        }

        RebuildCollections();
        RebuildChart();
    }

    partial void OnSelectedRangeChanged(HistoryRange value)
    {
        RefreshForCurrentRange();
    }

    private void RebuildCollections()
    {
        long rangeTotal = 0;
        foreach (var a in _lastTopApps) rangeTotal += a.TotalBytes;

        RangeTotalBytes = rangeTotal;
        RangeAppCount = _lastTopApps.Count;
        RangeAnomalyCount = _lastAnomalies.Count;
        HasData = _lastTimeseries.Count > 0 || _lastTopApps.Count > 0;

        // Build a lookup of first-seen timestamps so we can tag which
        // apps are "new" inside this range without a second DB query.
        var firstSeenMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var fs in _lastFirstSeen)
            firstSeenMap[fs.ProcessName] = fs.FirstSeenUtc;

        TopApps.Clear();
        foreach (var a in _lastTopApps)
        {
            DateTime? firstSeenLocal = null;
            var isNew = false;
            if (firstSeenMap.TryGetValue(a.ProcessName, out var fsUnix))
            {
                firstSeenLocal = DateTimeOffset.FromUnixTimeSeconds(fsUnix)
                    .UtcDateTime.ToLocalTime();
                isNew = true;
            }
            TopApps.Add(new HistoryAppRow(
                a.ProcessName, a.BytesSent, a.BytesReceived,
                rangeTotal, firstSeenLocal, isNew));
        }

        Anomalies.Clear();
        foreach (var an in _lastAnomalies)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(an.TimestampUtc)
                .UtcDateTime.ToLocalTime();
            Anomalies.Add(new HistoryAnomalyRow(
                local, an.ProcessName, an.HostDomain,
                an.Bytes, an.Mean, an.StdDev));
        }

        FirstSeen.Clear();
        foreach (var fs in _lastFirstSeen)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(fs.FirstSeenUtc)
                .UtcDateTime.ToLocalTime();
            FirstSeen.Add(new HistoryFirstSeenRow(fs.ProcessName, local));
        }
    }
    
    /// <summary>
    /// Called by the view on pointer move over the chart canvas.
    /// Snaps to the nearest data sample and updates the hover overlay
    /// state. The view renders the crosshair + label using these
    /// observable properties.
    /// </summary>
    public void UpdateHover(double cursorX, double canvasWidth)
    {
        if (_lastTimeseries.Count == 0 || canvasWidth <= 0)
        {
            IsHoverVisible = false;
            return;
        }

        var (idx, sample) = HistoryTimeseriesBuffer.HitTest(
            _lastTimeseries, cursorX, canvasWidth,
            _chartFirstBucketUtc, _chartLastBucketUtc);
        if (idx < 0)
        {
            IsHoverVisible = false;
            return;
        }

        var snappedX = HistoryTimeseriesBuffer.BucketToPixelX(
            sample.BucketUtc, _chartFirstBucketUtc, _chartLastBucketUtc, canvasWidth);
        HoverX = snappedX;

        // Bias the label to the other side of the crosshair when we're
        // within 80px of the right edge, so it doesn't clip off-canvas.
        const double labelWidth = 170;
        HoverLabelX = snappedX + labelWidth > canvasWidth
            ? snappedX - labelWidth - 8
            : snappedX + 8;

        var local = DateTimeOffset.FromUnixTimeSeconds(sample.BucketUtc)
            .UtcDateTime.ToLocalTime();
        HoverTimeText = FormatHoverTime(local);
        HoverValueText = $"{FormatBytes(sample.BytesTotal)}/min";

        // Surface any anomalies that fell inside this minute bucket so
        // the user can tell at a glance "that spike was X".
        var bucketStart = sample.BucketUtc;
        var bucketEnd = bucketStart + 60;
        var hits = new List<string>(2);
        foreach (var a in _lastAnomalies)
        {
            if (a.TimestampUtc >= bucketStart && a.TimestampUtc < bucketEnd)
            {
                hits.Add(a.ProcessName);
                if (hits.Count >= 2) break;
            }
        }
        HoverHasEvents = hits.Count > 0;
        HoverEventsText = hits.Count switch
        {
            0 => "",
            1 => $"spike: {hits[0]}",
            _ => $"spike: {hits[0]}, {hits[1]}"
        };

        IsHoverVisible = true;
    }

    public void HideHover() => IsHoverVisible = false;

    private static string FormatHoverTime(DateTime local)
    {
        var delta = DateTime.Now - local;
        if (delta < TimeSpan.FromHours(24) && delta >= TimeSpan.Zero)
            return local.ToString("HH:mm");
        if (delta < TimeSpan.FromDays(7))
            return local.ToString("ddd HH:mm");
        return local.ToString("MMM d HH:mm");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private void RebuildChart()
    {
        var data = HistoryTimeseriesBuffer.Build(
            _lastTimeseries, _lastAnomalies,
            _rangeFromUtc, _rangeToUtc,
            TimeseriesWidth, TimeseriesHeight);

        TimeseriesLinePoints = data.LinePoints;
        TimeseriesFillPoints = data.FillPoints;
        AxisLabels = data.AxisLabels;
        AnomalyMarkers = data.AnomalyMarkers;
        _chartFirstBucketUtc = data.FirstBucketUtc;
        _chartLastBucketUtc = data.LastBucketUtc;

        PeakLabel = data.PeakBytesPerMinute > 0
            ? $"peak {FormatBytes(data.PeakBytesPerMinute)}/min"
            : "";

        IsHoverVisible = false;
    }

    /// <summary>
    /// Re-run layout-dependent work when the chart surface resizes.
    /// Called by the view after a LayoutUpdated event with fresh bounds.
    /// </summary>
    public void ResizeChart(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        if (Math.Abs(width - TimeseriesWidth) < 0.5 &&
            Math.Abs(height - TimeseriesHeight) < 0.5) return;

        TimeseriesWidth = width;
        TimeseriesHeight = height;
        RebuildChart();
    }

    [RelayCommand]
    private void SelectRange(object? rangeParam)
    {
        // XAML CommandParameter arrives as a string literal ("Today",
        // "ThisCycle", etc.) because Avalonia's markup-side conversion
        // to an enum isn't reliable across all binding paths. Parse
        // here so the call site stays simple.
        if (rangeParam is HistoryRange r)
        {
            SelectedRange = r;
        }
        else if (rangeParam is string s &&
                 Enum.TryParse<HistoryRange>(s, ignoreCase: true, out var parsed))
        {
            SelectedRange = parsed;
        }
    }

    [RelayCommand]
    private void ToggleExportMenu() => IsExportMenuOpen = !IsExportMenuOpen;

    [RelayCommand]
    private async Task ExportCsv()
    {
        IsExportMenuOpen = false;
        await ExportAsync("csv");
    }

    [RelayCommand]
    private async Task ExportJson()
    {
        IsExportMenuOpen = false;
        await ExportAsync("json");
    }

    private async Task ExportAsync(string extension)
    {
        if (_pickSavePath is null) return;

        var suggested = BuildSuggestedFilename(extension);
        var path = await _pickSavePath(suggested, extension);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (extension == "csv")
            {
                HistoryExporter.WriteCsv(path, _rangeFromUtc, _rangeToUtc,
                    _lastTopApps, _lastTimeseries, _lastAnomalies, _lastFirstSeen);
            }
            else
            {
                HistoryExporter.WriteJson(path, _rangeFromUtc, _rangeToUtc,
                    _lastTopApps, _lastTimeseries, _lastAnomalies, _lastFirstSeen);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] export failed: {ex}");
        }
    }

    private string BuildSuggestedFilename(string extension)
    {
        var rangeSlug = SelectedRange switch
        {
            HistoryRange.Today => "today",
            HistoryRange.ThisCycle => "cycle",
            HistoryRange.Last7Days => "7days",
            HistoryRange.Last30Days => "30days",
            _ => "range"
        };
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        return $"desuwatch-{rangeSlug}-{stamp}.{extension}";
    }
}