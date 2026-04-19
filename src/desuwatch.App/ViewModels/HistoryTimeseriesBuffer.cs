using Avalonia;
using Avalonia.Collections;
using Desuwatch.Storage;

namespace Desuwatch.App.ViewModels;

/// <summary>
/// One tick mark on the chart's x-axis. X is in pixel space; Label is
/// the text to render (e.g. "19:00", "Mon").
/// </summary>
public readonly record struct ChartAxisLabel(double X, string Label);

/// <summary>
/// A persistent marker rendered on top of the timeseries line for an
/// anomaly. X/Y are pixel positions; ProcessName is surfaced as the
/// tooltip text for reassurance on hover.
/// </summary>
public readonly record struct ChartAnomalyMarker(double X, double Y, string ProcessName);

/// <summary>
/// Full result of a chart build: the polyline and fill polygon points
/// plus the axis label metadata and the peak value that the y-axis
/// scale is anchored to. Callers display the peak as a corner label
/// so the reader knows what the chart's top edge represents.
/// </summary>
public readonly record struct HistoryChartData(
    Points LinePoints,
    Points FillPoints,
    IReadOnlyList<ChartAxisLabel> AxisLabels,
    IReadOnlyList<ChartAnomalyMarker> AnomalyMarkers,
    long PeakBytesPerMinute,
    long FirstBucketUtc,
    long LastBucketUtc);

/// <summary>
/// Builds polyline points for the history view's timeseries chart from
/// a set of per-minute DB rows. Unlike <see cref="AggregateSparklineBuffer"/>
/// this is stateless — the buffer is owned by the DB query result, not
/// by a live ring buffer.
/// <para>
/// The x-axis anchors to the range of actual data rather than the
/// selected window, so a mostly-empty range doesn't crowd everything
/// into a thin slice on one edge. Within the data range, gaps stay
/// proportional so a quiet weekend still looks quiet.
/// </para>
/// </summary>
internal static class HistoryTimeseriesBuffer
{
public static HistoryChartData Build(
        IReadOnlyList<HistoryTimeseriesPoint> samples,
        IReadOnlyList<HistoryAnomaly> anomalies,
        DateTime fromUtc,
        DateTime toUtc,
        double width,
        double height)
    {
        var line = new Points();
        var fill = new Points();

        if (samples.Count == 0 || width <= 0 || height <= 0)
        {
            return new HistoryChartData(
                line, fill,
                Array.Empty<ChartAxisLabel>(),
                Array.Empty<ChartAnomalyMarker>(),
                0, 0, 0);
        }

        var firstUnix = samples[0].BucketUtc;
        var lastUnix = samples[samples.Count - 1].BucketUtc;
        var span = Math.Max(60, lastUnix - firstUnix);

        long max = 0;
        for (var i = 0; i < samples.Count; i++)
            if (samples[i].BytesTotal > max) max = samples[i].BytesTotal;

        var labels = BuildAxisLabels(firstUnix, span, width);

        if (max == 0)
        {
            line.Add(new Point(0, height - 1));
            line.Add(new Point(width, height - 1));
            return new HistoryChartData(
                line, fill, labels,
                Array.Empty<ChartAnomalyMarker>(),
                0, firstUnix, lastUnix);
        }

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var xFrac = samples.Count == 1
                ? 0.0
                : Math.Clamp((double)(s.BucketUtc - firstUnix) / span, 0, 1);
            var x = xFrac * width;
            var normalized = (double)s.BytesTotal / max;
            var y = height - 1 - (normalized * (height - 2));
            line.Add(new Point(x, y));
        }

        if (line.Count > 0)
        {
            var first = line[0];
            var last = line[line.Count - 1];
            fill.Add(new Point(first.X, height - 1));
            for (var i = 0; i < line.Count; i++)
                fill.Add(line[i]);
            fill.Add(new Point(last.X, height - 1));
        }

        var markers = BuildAnomalyMarkers(
            anomalies, samples, line, firstUnix, lastUnix, width, height);

        return new HistoryChartData(
            line, fill, labels, markers, max, firstUnix, lastUnix);
    }

    /// <summary>
    /// Picks the nearest data sample to a given pixel X. Used by the
    /// hover overlay to map cursor position back to a data point.
    /// Returns (-1, default) when the sample list is empty.
    /// </summary>
    public static (int index, HistoryTimeseriesPoint sample) HitTest(
        IReadOnlyList<HistoryTimeseriesPoint> samples,
        double cursorX,
        double width,
        long firstUnix,
        long lastUnix)
    {
        if (samples.Count == 0 || width <= 0)
            return (-1, default);

        var span = Math.Max(60, lastUnix - firstUnix);
        var targetUnix = firstUnix + (long)(cursorX / width * span);

        var bestIdx = 0;
        var bestDelta = long.MaxValue;
        for (var i = 0; i < samples.Count; i++)
        {
            var delta = Math.Abs(samples[i].BucketUtc - targetUnix);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIdx = i;
            }
        }

        return (bestIdx, samples[bestIdx]);
    }

    /// <summary>
    /// Maps a data bucket's Unix timestamp back to its pixel X
    /// coordinate, using the same math as the line-point layout. Used
    /// by the hover overlay to position the crosshair on the snapped
    /// sample rather than on the raw cursor position.
    /// </summary>
    public static double BucketToPixelX(
        long bucketUnix,
        long firstUnix,
        long lastUnix,
        double width)
    {
        var span = Math.Max(60, lastUnix - firstUnix);
        var frac = Math.Clamp((double)(bucketUnix - firstUnix) / span, 0, 1);
        return frac * width;
    }
    
    /// <summary>
    /// Computes pixel positions for each anomaly marker. Dots sit on
    /// the line itself by looking up the y-coordinate of the sample
    /// closest in time to the anomaly's timestamp. Bucket the
    /// anomalies so we emit at most one marker per sample index —
    /// multiple anomalies inside the same minute stack visually into
    /// a single dot, with the hover tooltip surfacing the full list.
    /// </summary>
    private static IReadOnlyList<ChartAnomalyMarker> BuildAnomalyMarkers(
        IReadOnlyList<HistoryAnomaly> anomalies,
        IReadOnlyList<HistoryTimeseriesPoint> samples,
        Points line,
        long firstUnix,
        long lastUnix,
        double width,
        double height)
    {
        if (anomalies.Count == 0 || line.Count == 0)
            return Array.Empty<ChartAnomalyMarker>();

        var emitted = new HashSet<int>();
        var result = new List<ChartAnomalyMarker>();

        foreach (var a in anomalies)
        {
            // Find the nearest sample index by timestamp.
            var bestIdx = 0;
            var bestDelta = long.MaxValue;
            for (var i = 0; i < samples.Count; i++)
            {
                var delta = Math.Abs(samples[i].BucketUtc - a.TimestampUtc);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }

            // Don't double-mark the same sample if multiple anomalies
            // landed in one minute bucket.
            if (!emitted.Add(bestIdx)) continue;

            // Guard against index drift if samples and line somehow
            // desync (shouldn't happen — they're built in lockstep).
            if (bestIdx >= line.Count) continue;

            var pt = line[bestIdx];
            result.Add(new ChartAnomalyMarker(pt.X, pt.Y, a.ProcessName));
        }

        return result;
    }

    private static IReadOnlyList<ChartAxisLabel> BuildAxisLabels(
        long firstUnix, long span, double width)
    {
        var spanHours = span / 3600.0;
        string format;
        if (spanHours <= 36) format = "HH:mm";
        else if (spanHours <= 24 * 10) format = "ddd";
        else format = "MMM d";

        const int target = 4;
        const double edgeInset = 6;
        // Approximate label width for the longest format ("MMM d" ~ 36px,
        // "HH:mm" ~ 28px, "ddd" ~ 20px). We use this to pull the first
        // and last labels inside the canvas by their own half-width
        // instead of nudging everything by a fixed amount in XAML, so
        // edge labels don't get clipped at the canvas boundaries.
        var labelWidth = format == "MMM d" ? 36.0 : format == "HH:mm" ? 28.0 : 20.0;
        var labels = new List<ChartAxisLabel>(target);

        for (var i = 0; i < target; i++)
        {
            var frac = (double)i / (target - 1);
            var unix = firstUnix + (long)(frac * span);
            var local = DateTimeOffset.FromUnixTimeSeconds(unix)
                .UtcDateTime.ToLocalTime();

            double x;
            if (i == 0)
            {
                // First label: left-align at the canvas edge with a
                // small inset so it isn't glued to x=0.
                x = edgeInset;
            }
            else if (i == target - 1)
            {
                // Last label: right-align by pulling back by its full
                // width so the text ends near the canvas edge.
                x = width - labelWidth - edgeInset;
            }
            else
            {
                // Middle labels: centered on their tick position by
                // pulling back by half the label width.
                x = frac * width - (labelWidth / 2);
            }

            labels.Add(new ChartAxisLabel(x, local.ToString(format)));
        }

        return labels;
    }
}