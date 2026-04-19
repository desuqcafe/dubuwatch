using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Desuwatch.Storage;

/// <summary>
/// Writes history query results to disk as CSV or JSON. Scope is
/// deliberately matched to the UI's history view: the exported data is
/// the same data the user is looking at, aggregated by the same range.
/// Per-minute raw detail stays in the database for future query needs.
/// </summary>
public static class HistoryExporter
{
    public static void WriteCsv(
        string path,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<HistoryAppTotal> topApps,
        IReadOnlyList<HistoryTimeseriesPoint> timeseries,
        IReadOnlyList<HistoryAnomaly> anomalies,
        IReadOnlyList<HistoryFirstSeen> firstSeen)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        // Header: export metadata so the file is self-describing if it
        // gets detached from context later.
        writer.WriteLine($"# desuwatch history export");
        writer.WriteLine($"# range_utc_from,{FormatUtc(fromUtc)}");
        writer.WriteLine($"# range_utc_to,{FormatUtc(toUtc)}");
        writer.WriteLine($"# exported_utc,{FormatUtc(DateTime.UtcNow)}");
        writer.WriteLine();

        writer.WriteLine("# section,top_apps");
        writer.WriteLine("process_name,bytes_sent,bytes_received,bytes_total");
        foreach (var a in topApps)
        {
            writer.Write(EscapeCsv(a.ProcessName));
            writer.Write(',');
            writer.Write(a.BytesSent.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(a.BytesReceived.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(a.TotalBytes.ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteLine();

        writer.WriteLine("# section,timeseries");
        writer.WriteLine("bucket_utc_iso,bucket_unix_seconds,bytes_total");
        foreach (var p in timeseries)
        {
            var iso = FormatUtc(DateTimeOffset.FromUnixTimeSeconds(p.BucketUtc).UtcDateTime);
            writer.Write(iso);
            writer.Write(',');
            writer.Write(p.BucketUtc.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(p.BytesTotal.ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteLine();

        writer.WriteLine("# section,anomalies");
        writer.WriteLine("timestamp_utc_iso,process_name,host_domain,bytes,mean,stddev");
        foreach (var an in anomalies)
        {
            var iso = FormatUtc(DateTimeOffset.FromUnixTimeSeconds(an.TimestampUtc).UtcDateTime);
            writer.Write(iso);
            writer.Write(',');
            writer.Write(EscapeCsv(an.ProcessName));
            writer.Write(',');
            writer.Write(EscapeCsv(an.HostDomain ?? ""));
            writer.Write(',');
            writer.Write(an.Bytes.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(an.Mean.ToString("0.##", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(an.StdDev.ToString("0.##", CultureInfo.InvariantCulture));
        }
        writer.WriteLine();

        writer.WriteLine("# section,first_seen");
        writer.WriteLine("process_name,first_seen_utc_iso,last_seen_utc_iso");
        foreach (var fs in firstSeen)
        {
            writer.Write(EscapeCsv(fs.ProcessName));
            writer.Write(',');
            writer.Write(FormatUtc(DateTimeOffset.FromUnixTimeSeconds(fs.FirstSeenUtc).UtcDateTime));
            writer.Write(',');
            writer.WriteLine(FormatUtc(DateTimeOffset.FromUnixTimeSeconds(fs.LastSeenUtc).UtcDateTime));
        }
    }

    public static void WriteJson(
        string path,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<HistoryAppTotal> topApps,
        IReadOnlyList<HistoryTimeseriesPoint> timeseries,
        IReadOnlyList<HistoryAnomaly> anomalies,
        IReadOnlyList<HistoryFirstSeen> firstSeen)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var payload = new
        {
            range = new
            {
                from_utc = FormatUtc(fromUtc),
                to_utc = FormatUtc(toUtc),
                exported_utc = FormatUtc(DateTime.UtcNow)
            },
            top_apps = topApps.Select(a => new
            {
                process_name = a.ProcessName,
                bytes_sent = a.BytesSent,
                bytes_received = a.BytesReceived,
                bytes_total = a.TotalBytes
            }),
            timeseries = timeseries.Select(p => new
            {
                bucket_utc = FormatUtc(DateTimeOffset.FromUnixTimeSeconds(p.BucketUtc).UtcDateTime),
                bucket_unix_seconds = p.BucketUtc,
                bytes_total = p.BytesTotal
            }),
            anomalies = anomalies.Select(an => new
            {
                timestamp_utc = FormatUtc(DateTimeOffset.FromUnixTimeSeconds(an.TimestampUtc).UtcDateTime),
                process_name = an.ProcessName,
                host_domain = an.HostDomain,
                bytes = an.Bytes,
                mean = an.Mean,
                stddev = an.StdDev
            }),
            first_seen = firstSeen.Select(fs => new
            {
                process_name = fs.ProcessName,
                first_seen_utc = FormatUtc(DateTimeOffset.FromUnixTimeSeconds(fs.FirstSeenUtc).UtcDateTime),
                last_seen_utc = FormatUtc(DateTimeOffset.FromUnixTimeSeconds(fs.LastSeenUtc).UtcDateTime)
            })
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string FormatUtc(DateTime utc)
    {
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        // Quote if contains comma, quote, newline, or leading/trailing whitespace.
        var needsQuoting =
            field.IndexOf(',') >= 0 ||
            field.IndexOf('"') >= 0 ||
            field.IndexOf('\n') >= 0 ||
            field.IndexOf('\r') >= 0;
        if (!needsQuoting) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
}