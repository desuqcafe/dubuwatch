using Microsoft.Data.Sqlite;

namespace Desuwatch.Storage;

/// <summary>
/// SQLite-backed persistence for the reverse-DNS resolver. Positive
/// resolutions are retained up to <c>retention</c>; negatives are capped
/// to a 15-minute window on load so stale cooldown entries don't prevent
/// retry forever.
///
/// Buffered writes flush on a <see cref="PeriodicTimer"/> identical in
/// cadence to <see cref="HistoryStore"/>. Safe for calls from any thread.
/// </summary>
public sealed class DnsCacheStore : IDnsCachePersistence, IDisposable
{
    private readonly string _dbPath;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _positiveRetention;
    private readonly PendingDnsWrites _pending = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _connectionString;

    private Task? _flushWorker;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public DnsCacheStore(string dbPath, TimeSpan flushInterval, TimeSpan positiveRetention)
    {
        _dbPath = dbPath;
        _flushInterval = flushInterval;
        _positiveRetention = positiveRetention;

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

    public DnsCacheHydration Hydrate(TimeSpan negativeMaxAge)
    {
        var positives = new Dictionary<string, string>(StringComparer.Ordinal);
        var negatives = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        try
        {
            using var conn = OpenConnection();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ip, hostname FROM dns_positive";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    positives[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var negativeCutoff = ToUnixSeconds(DateTime.UtcNow - negativeMaxAge);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT ip, failed_at_utc
                    FROM dns_negative
                    WHERE failed_at_utc >= $cutoff";
                cmd.Parameters.AddWithValue("$cutoff", negativeCutoff);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var ip = reader.GetString(0);
                    var failedAt = FromUnixSeconds(reader.GetInt64(1));
                    negatives[ip] = failedAt;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] DNS cache hydration failed: {ex}");
            return DnsCacheHydration.Empty;
        }

        return new DnsCacheHydration(positives, negatives);
    }

    public void RecordPositive(string ipAddress, string hostname)
    {
        if (string.IsNullOrEmpty(ipAddress) || string.IsNullOrEmpty(hostname)) return;
        var now = ToUnixSeconds(DateTime.UtcNow);
        lock (_lock)
        {
            _pending.AddPositive(ipAddress, hostname, now);
        }
    }

    public void RecordNegative(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress)) return;
        var now = ToUnixSeconds(DateTime.UtcNow);
        lock (_lock)
        {
            _pending.AddNegative(ipAddress, now);
        }
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = DnsCacheSchema.CreateTablesSql;
        cmd.ExecuteNonQuery();

        using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = @"
            INSERT INTO schema_meta (key, value) VALUES ('version', $v)
            ON CONFLICT(key) DO UPDATE SET value = $v";
        versionCmd.Parameters.AddWithValue("$v", DnsCacheSchema.CurrentVersion.ToString());
        versionCmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = DnsCacheSchema.ConnectionPragmas;
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
            System.Diagnostics.Debug.WriteLine($"[desuwatch] DNS cache flush loop crashed: {ex}");
        }
    }

    private void FlushOnce()
    {
        List<KeyValuePair<string, PendingDnsWrites.PositiveEntry>> positives;
        List<KeyValuePair<string, long>> negatives;

        lock (_lock)
        {
            if (_pending.IsEmpty) return;
            positives = _pending.DrainPositives().ToList();
            negatives = _pending.DrainNegatives().ToList();
        }

        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            WritePositives(conn, tx, positives);
            WriteNegatives(conn, tx, negatives);

            tx.Commit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] DNS cache flush failed: {ex}");
        }
    }

    private static void WritePositives(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<KeyValuePair<string, PendingDnsWrites.PositiveEntry>> positives)
    {
        if (positives.Count == 0) return;

        // Clear any stale negative for these IPs first, then upsert positives.
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM dns_negative WHERE ip = $ip";
            var pIp = delCmd.Parameters.Add("$ip", SqliteType.Text);
            foreach (var kv in positives)
            {
                pIp.Value = kv.Key;
                delCmd.ExecuteNonQuery();
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO dns_positive (ip, hostname, resolved_at_utc)
            VALUES ($ip, $h, $t)
            ON CONFLICT(ip)
            DO UPDATE SET
                hostname        = excluded.hostname,
                resolved_at_utc = excluded.resolved_at_utc";

        var pIpMain = cmd.Parameters.Add("$ip", SqliteType.Text);
        var pHost = cmd.Parameters.Add("$h", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$t", SqliteType.Integer);

        foreach (var kv in positives)
        {
            pIpMain.Value = kv.Key;
            pHost.Value = kv.Value.Hostname;
            pTime.Value = kv.Value.ResolvedAtUtc;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteNegatives(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<KeyValuePair<string, long>> negatives)
    {
        if (negatives.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO dns_negative (ip, failed_at_utc)
            VALUES ($ip, $t)
            ON CONFLICT(ip)
            DO UPDATE SET failed_at_utc = excluded.failed_at_utc";

        var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$t", SqliteType.Integer);

        foreach (var kv in negatives)
        {
            pIp.Value = kv.Key;
            pTime.Value = kv.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private void MaybePrune()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromHours(1)) return;
        _lastPruneUtc = now;

        // Negatives match the resolver's in-memory 15-min TTL. Positives
        // use the configured retention window.
        var positiveCutoff = ToUnixSeconds(now - _positiveRetention);
        var negativeCutoff = ToUnixSeconds(now - TimeSpan.FromMinutes(15));

        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = DnsCacheSchema.PruneSql;
            cmd.Parameters.AddWithValue("$positive_cutoff", positiveCutoff);
            cmd.Parameters.AddWithValue("$negative_cutoff", negativeCutoff);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] DNS cache prune failed: {ex}");
        }
    }

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateTime FromUnixSeconds(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

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

        FlushOnce();

        _cts.Dispose();
    }
}