using Avalonia;
using Avalonia.Collections;

namespace Desuwatch.App.ViewModels;

/// <summary>
/// Fixed-capacity ring buffer feeding the full-width heartbeat chart in
/// the "Today" hero card. Shape is intentionally simpler than
/// <see cref="SparklineBuffer"/> — no anomaly detection, no warm-up —
/// because the chart summarises aggregate throughput across all apps
/// and never needs per-sample statistics.
///
/// Callers push one sample per tick (bytes/sec this tick, summed across
/// all apps). The <see cref="BuildPoints"/> method produces a fresh
/// <see cref="Points"/> instance scaled to the supplied width/height;
/// Avalonia's Polyline does not observe mutations to an existing Points
/// collection so a new instance is required on each update.
/// </summary>
internal sealed class AggregateSparklineBuffer
{
    private readonly long[] _samples;
    private int _head;
    private int _count;

    public int Capacity { get; }
    public int Count => _count;

    public AggregateSparklineBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new long[capacity];
        Capacity = capacity;
    }

    public void Push(long sample)
    {
        _samples[_head] = sample;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    public long Max()
    {
        long max = 0;
        for (var i = 0; i < _count; i++)
            if (_samples[i] > max) max = _samples[i];
        return max;
    }

    /// <summary>
    /// Builds a polyline that stretches the current buffer across the
    /// supplied pixel dimensions. The line is anchored at the bottom
    /// and rises toward the top as throughput increases, with 1px of
    /// headroom and 1px of baseline padding.
    /// </summary>
    public Points BuildPoints(double width, double height)
    {
        var points = new Points();
        if (_count == 0) return points;

        var max = Max();
        var start = (_head - _count + Capacity) % Capacity;

        // No traffic anywhere in the window — render a flat baseline so
        // the chart doesn't collapse to a single dot.
        if (max == 0)
        {
            for (var i = 0; i < _count; i++)
            {
                var x = (double)i / (Capacity - 1) * width;
                points.Add(new Point(x, height - 1));
            }
            return points;
        }

        for (var i = 0; i < _count; i++)
        {
            var sample = _samples[(start + i) % Capacity];
            var x = (double)i / (Capacity - 1) * width;
            var normalized = (double)sample / max;
            var y = height - 1 - (normalized * (height - 2));
            points.Add(new Point(x, y));
        }

        return points;
    }

    /// <summary>
    /// Snapshot the most recent sample pushed. Convenience for status
    /// labels that want to echo "right now" throughput without walking
    /// the full buffer.
    /// </summary>
    public long Current()
    {
        if (_count == 0) return 0;
        var newestIdx = (_head - 1 + Capacity) % Capacity;
        return _samples[newestIdx];
    }

    public void Clear()
    {
        Array.Clear(_samples, 0, _samples.Length);
        _head = 0;
        _count = 0;
    }
}