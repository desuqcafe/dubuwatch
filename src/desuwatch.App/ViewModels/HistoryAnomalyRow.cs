namespace Desuwatch.App.ViewModels;

/// <summary>
/// One row in the history panel's "notable events" list. Formats an
/// anomaly record for display: relative time, the app that triggered it,
/// and a baseline-aware summary of how much it was over the line.
/// </summary>
public sealed class HistoryAnomalyRow
{
    public DateTime TimestampLocal { get; }
    public string ProcessName { get; }
    public string? HostDomain { get; }
    public long Bytes { get; }
    public double Mean { get; }
    public double StdDev { get; }

    public HistoryAnomalyRow(
        DateTime timestampLocal,
        string processName,
        string? hostDomain,
        long bytes,
        double mean,
        double stdDev)
    {
        TimestampLocal = timestampLocal;
        ProcessName = processName;
        HostDomain = hostDomain;
        Bytes = bytes;
        Mean = mean;
        StdDev = stdDev;
    }

    public string TimeLabel => FormatRelative(TimestampLocal, DateTime.Now);

    public string DetailText
    {
        get
        {
            var spike = FormatBytes(Bytes);
            var baseline = FormatBytes((long)Mean);
            var host = string.IsNullOrEmpty(HostDomain) ? "" : $" · {HostDomain}";
            return $"{spike} spike · baseline {baseline}/s{host}";
        }
    }

    private static string FormatRelative(DateTime when, DateTime now)
    {
        var delta = now - when;
        if (delta < TimeSpan.Zero) return when.ToString("MMM d HH:mm");
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromHours(24))
        {
            var hours = (int)delta.TotalHours;
            return hours == 1 ? "1h ago" : $"{hours}h ago";
        }
        if (delta < TimeSpan.FromDays(7))
            return when.ToString("ddd HH:mm");
        return when.ToString("MMM d");
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
}