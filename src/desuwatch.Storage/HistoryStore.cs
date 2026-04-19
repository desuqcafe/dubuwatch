using Microsoft.Data.Sqlite;

namespace Desuwatch.Storage;

/// <summary>
/// Owns the SQLite history database. Buffers deltas in memory and flushes
/// them on a background timer to minimize write amplification. Safe for
/// calls from any thread; internal state is protected by a single lock.
///
/// Flush cadence is configurable but defaults to 60 seconds — one flush
/// per minute-bucket boundary, matching the rollup granularity.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly string _dbPath;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _retention;
    private readonly PendingRollup _pending = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _connectionString;

    private Task? _flushWorker;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public HistoryStore(string dbPath, TimeSpan flushInterval, TimeSpan retention)
    {
        _dbPath = dbPath;
        _flushInterval = flushInterval;
        _retention = retention;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
    }

    public void Start()
    {
        if (_flushWorker is not null) return;
        _flushWorker = Task.Run(() => FlushLoop(_cts.Token));
    }

    /// <summary>
    /// Records a per-tick delta for a process. Typically the main view
    /// model calls this once per second per app. Bucket is the start of
    /// the containing UTC minute.
    /// </summary>
    public void RecordAppDelta(DateTime tickTimestampUtc, string processName, long sent, long received)
    {
        if (sent == 0 && received == 0) return;
        var bucket = ToMinuteBucket(tickTimestampUtc);
        lock (_lock)
        {
            _pending.AddAppDelta(bucket, processName, sent, received);
            _pending.TouchRegistry(processName, ToUnixSeconds(tickTimestampUtc));
        }
    }

    public void RecordHostDelta(
        DateTime tickTimestampUtc,
        string processName,
        string hostDomain,
        long sent,
        long received)
    {
        if (sent == 0 && received == 0) return;
        var bucket = ToMinuteBucket(tickTimestampUtc);
        lock (_lock)
        {
            _pending.AddHostDelta(bucket, processName, hostDomain, sent, received);
        }
    }

    public void RecordAnomaly(AnomalyRecord anomaly)
    {
        lock (_lock)
        {
            _pending.AddAnomaly(anomaly);
        }
    }

    /// <summary>
    /// Returns total bytes for a process across the given UTC window.
    /// Reads from the database only — does not include the in-memory
    /// pending buffer. Callers requiring live-accurate totals should add
    /// the current session's running counters.
    /// </summary>
    public (long sent, long received) GetAppTotals(string processName, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(bytes_sent), 0), COALESCE(SUM(bytes_received), 0)
            FROM app_usage_minute
            WHERE process_name = $p AND bucket_utc BETWEEN $from AND $to";
        cmd.Parameters.AddWithValue("$p", processName);
        cmd.Parameters.AddWithValue("$from", ToMinuteBucket(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToMinuteBucket(toUtc));

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Returns total bytes across all processes for the given UTC window.
    /// Reads from the database only — does not include the in-memory
    /// pending buffer. Callers needing live-accurate totals should add
    /// the current session's running counters on top.
    /// </summary>
    public (long sent, long received) GetTotalsAcrossApps(DateTime fromUtc, DateTime toUtc)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(bytes_sent), 0), COALESCE(SUM(bytes_received), 0)
            FROM app_usage_minute
            WHERE bucket_utc BETWEEN $from AND $to";
        cmd.Parameters.AddWithValue("$from", ToMinuteBucket(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToMinuteBucket(toUtc));

using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Returns per-minute aggregate totals (sent + received across all
    /// apps) for the given UTC window, in chronological order. Each
    /// returned point corresponds to one minute bucket that had at
    /// least one byte recorded; empty buckets are not returned.
    /// <para>
    /// Callers rendering a timeseries chart over a fixed range should
    /// fill gaps client-side — this is cheaper than emitting zero
    /// rows for every empty minute in a 30-day window.
    /// </para>
    /// </summary>
    public IReadOnlyList<HistoryTimeseriesPoint> GetTimeseries(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<HistoryTimeseriesPoint>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT bucket_utc, SUM(bytes_sent + bytes_received) AS total
            FROM app_usage_minute
            WHERE bucket_utc BETWEEN $from AND $to
            GROUP BY bucket_utc
            ORDER BY bucket_utc ASC";
        cmd.Parameters.AddWithValue("$from", ToMinuteBucket(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToMinuteBucket(toUtc));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryTimeseriesPoint(
                reader.GetInt64(0),
                reader.GetInt64(1)));
        }
        return result;
    }

    /// <summary>
    /// Returns per-app totals for the given UTC window, sorted by total
    /// bytes descending. Pass <paramref name="limit"/> to cap the result
    /// size; pass 0 or negative to return all rows.
    /// </summary>
    public IReadOnlyList<HistoryAppTotal> GetTopApps(
        DateTime fromUtc, DateTime toUtc, int limit = 50)
    {
        var result = new List<HistoryAppTotal>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = limit > 0
            ? @"
                SELECT process_name,
                       COALESCE(SUM(bytes_sent), 0),
                       COALESCE(SUM(bytes_received), 0)
                FROM app_usage_minute
                WHERE bucket_utc BETWEEN $from AND $to
                GROUP BY process_name
                ORDER BY SUM(bytes_sent + bytes_received) DESC
                LIMIT $limit"
            : @"
                SELECT process_name,
                       COALESCE(SUM(bytes_sent), 0),
                       COALESCE(SUM(bytes_received), 0)
                FROM app_usage_minute
                WHERE bucket_utc BETWEEN $from AND $to
                GROUP BY process_name
                ORDER BY SUM(bytes_sent + bytes_received) DESC";
        cmd.Parameters.AddWithValue("$from", ToMinuteBucket(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToMinuteBucket(toUtc));
        if (limit > 0)
            cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryAppTotal(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }
        return result;
    }

    /// <summary>
    /// Returns anomaly records inside the given UTC window, newest first.
    /// Pass <paramref name="limit"/> to cap result size.
    /// </summary>
    public IReadOnlyList<HistoryAnomaly> GetAnomalies(
        DateTime fromUtc, DateTime toUtc, int limit = 100)
    {
        var result = new List<HistoryAnomaly>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT timestamp_utc, process_name, host_domain, bytes, mean, stddev
            FROM anomalies
            WHERE timestamp_utc BETWEEN $from AND $to
            ORDER BY timestamp_utc DESC
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$from", ToUnixSeconds(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnixSeconds(toUtc));
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryAnomaly(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }
        return result;
    }

    /// <summary>
    /// Returns apps whose first-seen timestamp falls inside the given
    /// UTC window, newest first. Used for the "new this period" section
    /// of the history view.
    /// </summary>
    public IReadOnlyList<HistoryFirstSeen> GetFirstSeenInRange(
        DateTime fromUtc, DateTime toUtc, int limit = 20)
    {
        var result = new List<HistoryFirstSeen>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT process_name, first_seen_utc, last_seen_utc
            FROM app_registry
            WHERE first_seen_utc BETWEEN $from AND $to
            ORDER BY first_seen_utc DESC
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$from", ToUnixSeconds(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToUnixSeconds(toUtc));
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryFirstSeen(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }
        return result;
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = HistorySchema.CreateTablesSql;
        cmd.ExecuteNonQuery();

        using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = @"
            INSERT INTO schema_meta (key, value) VALUES ('version', $v)
            ON CONFLICT(key) DO UPDATE SET value = $v";
        versionCmd.Parameters.AddWithValue("$v", HistorySchema.CurrentVersion.ToString());
        versionCmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = HistorySchema.ConnectionPragmas;
        pragma.ExecuteNonQuery();
        return conn;
    }

    private async Task FlushLoop(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(_flushInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                FlushOnce();
                MaybePrune();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] history flush loop crashed: {ex}");
        }
    }

    private void FlushOnce()
    {
        // Copy pending state under the lock, then do the I/O outside so we
        // don't block event recording during the write.
        List<KeyValuePair<PendingRollup.AppKey, PendingRollup.Totals>> apps;
        List<KeyValuePair<PendingRollup.HostKey, PendingRollup.Totals>> hosts;
        List<KeyValuePair<string, (long firstSeen, long lastSeen)>> registry;
        List<AnomalyRecord> anomalies;

        lock (_lock)
        {
            if (_pending.IsEmpty) return;
            apps = _pending.DrainApps().ToList();
            hosts = _pending.DrainHosts().ToList();
            registry = _pending.DrainRegistry().ToList();
            anomalies = _pending.DrainAnomalies().ToList();
        }

        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            WriteAppRollups(conn, tx, apps);
            WriteHostRollups(conn, tx, hosts);
            WriteRegistry(conn, tx, registry);
            WriteAnomalies(conn, tx, anomalies);

            tx.Commit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] history flush failed: {ex}");
            // Note: drained batch is lost. Acceptable for telemetry data;
            // the alternative (re-enqueue on failure) risks unbounded memory
            // growth if the DB is persistently unhealthy.
        }
    }

    private static void WriteAppRollups(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<KeyValuePair<PendingRollup.AppKey, PendingRollup.Totals>> apps)
    {
        if (apps.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO app_usage_minute (bucket_utc, process_name, bytes_sent, bytes_received)
            VALUES ($b, $p, $s, $r)
            ON CONFLICT(bucket_utc, process_name)
            DO UPDATE SET
                bytes_sent     = bytes_sent     + excluded.bytes_sent,
                bytes_received = bytes_received + excluded.bytes_received";

        var pBucket = cmd.Parameters.Add("$b", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$p", SqliteType.Text);
        var pSent = cmd.Parameters.Add("$s", SqliteType.Integer);
        var pRecv = cmd.Parameters.Add("$r", SqliteType.Integer);

        foreach (var kv in apps)
        {
            pBucket.Value = kv.Key.BucketUtc;
            pName.Value = kv.Key.ProcessName;
            pSent.Value = kv.Value.Sent;
            pRecv.Value = kv.Value.Received;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteHostRollups(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<KeyValuePair<PendingRollup.HostKey, PendingRollup.Totals>> hosts)
    {
        if (hosts.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO host_usage_minute (bucket_utc, process_name, host_domain, bytes_sent, bytes_received)
            VALUES ($b, $p, $h, $s, $r)
            ON CONFLICT(bucket_utc, process_name, host_domain)
            DO UPDATE SET
                bytes_sent     = bytes_sent     + excluded.bytes_sent,
                bytes_received = bytes_received + excluded.bytes_received";

        var pBucket = cmd.Parameters.Add("$b", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$p", SqliteType.Text);
        var pHost = cmd.Parameters.Add("$h", SqliteType.Text);
        var pSent = cmd.Parameters.Add("$s", SqliteType.Integer);
        var pRecv = cmd.Parameters.Add("$r", SqliteType.Integer);

        foreach (var kv in hosts)
        {
            pBucket.Value = kv.Key.BucketUtc;
            pName.Value = kv.Key.ProcessName;
            pHost.Value = kv.Key.HostDomain;
            pSent.Value = kv.Value.Sent;
            pRecv.Value = kv.Value.Received;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteRegistry(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<KeyValuePair<string, (long firstSeen, long lastSeen)>> registry)
    {
        if (registry.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO app_registry (process_name, first_seen_utc, last_seen_utc)
            VALUES ($p, $f, $l)
            ON CONFLICT(process_name)
            DO UPDATE SET last_seen_utc = excluded.last_seen_utc";

        var pName = cmd.Parameters.Add("$p", SqliteType.Text);
        var pFirst = cmd.Parameters.Add("$f", SqliteType.Integer);
        var pLast = cmd.Parameters.Add("$l", SqliteType.Integer);

        foreach (var kv in registry)
        {
            pName.Value = kv.Key;
            pFirst.Value = kv.Value.firstSeen;
            pLast.Value = kv.Value.lastSeen;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteAnomalies(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<AnomalyRecord> anomalies)
    {
        if (anomalies.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO anomalies (timestamp_utc, process_name, host_domain, bytes, mean, stddev)
            VALUES ($t, $p, $h, $b, $m, $d)";

        var pTs = cmd.Parameters.Add("$t", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$p", SqliteType.Text);
        var pHost = cmd.Parameters.Add("$h", SqliteType.Text);
        var pBytes = cmd.Parameters.Add("$b", SqliteType.Integer);
        var pMean = cmd.Parameters.Add("$m", SqliteType.Real);
        var pStd = cmd.Parameters.Add("$d", SqliteType.Real);

        foreach (var a in anomalies)
        {
            pTs.Value = ToUnixSeconds(a.TimestampUtc);
            pName.Value = a.ProcessName;
            pHost.Value = (object?)a.HostDomain ?? DBNull.Value;
            pBytes.Value = a.Bytes;
            pMean.Value = a.Mean;
            pStd.Value = a.StdDev;
            cmd.ExecuteNonQuery();
        }
    }

    private void MaybePrune()
    {
        // Prune at most once per hour. Pruning is cheap (indexed DELETE)
        // but not so cheap we need to do it on every flush.
        var now = DateTime.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromHours(1)) return;
        _lastPruneUtc = now;

        var cutoff = ToMinuteBucket(now - _retention);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = HistorySchema.PruneSql;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] prune failed: {ex}");
        }
    }

    private static long ToMinuteBucket(DateTime utc)
    {
        var rounded = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        return ToUnixSeconds(rounded);
    }

    private static long ToUnixSeconds(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _flushWorker?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Swallow shutdown-time exceptions.
        }

        // Final flush so we don't lose the last partial minute on clean exit.
        FlushOnce();

        _cts.Dispose();
    }
}