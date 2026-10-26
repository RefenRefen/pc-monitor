using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcMonitor.Collector.Aggregation;
using PcMonitor.Core;

namespace PcMonitor.Collector.Storage;

internal readonly record struct RetentionResult(int MinuteRowsRolledUp, int QuarterRowsDeleted, int ConnectionsDeleted, int AppsDeleted);

/// <summary>
/// SQLite persistence (WAL). Minute rows are kept 7 days, then rolled up into 15 minute rows kept 90 days.
/// All access is serialised through one connection; writes are one small transaction per minute.
/// </summary>
internal sealed class UsageDatabase : IDisposable
{
    public const int MinuteRetentionDays = 7;
    public const int QuarterRetentionDays = 90;
    public const int QuarterSeconds = 900;

    private readonly object _gate = new();
    private readonly SqliteConnection _db;

    public string Path { get; }

    public UsageDatabase(string path)
    {
        Path = path;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Initialize();
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            // auto_vacuum must be chosen before the first table exists.
            Exec("PRAGMA auto_vacuum = INCREMENTAL;");
            Exec("PRAGMA journal_mode = WAL;");
            Exec("PRAGMA synchronous = NORMAL;");
            Exec("PRAGMA cache_size = -2048;");      // ~2 MB page cache
            Exec("PRAGMA temp_store = MEMORY;");
            Exec("PRAGMA mmap_size = 0;");

            const string usageCols = @"
                app_id INTEGER NOT NULL, ts INTEGER NOT NULL, dur REAL NOT NULL,
                cpu_avg REAL NOT NULL, cpu_max REAL NOT NULL,
                ram_avg REAL NOT NULL, ram_max REAL NOT NULL, commit_max REAL NOT NULL,
                gpu_avg REAL NOT NULL, gpu_max REAL NOT NULL, vram_max REAL NOT NULL,
                net_rx INTEGER NOT NULL, net_tx INTEGER NOT NULL, disk_r INTEGER NOT NULL, disk_w INTEGER NOT NULL,
                PRIMARY KEY (app_id, ts)";
            const string sysCols = @"
                ts INTEGER PRIMARY KEY, dur REAL NOT NULL,
                cpu_avg REAL NOT NULL, cpu_max REAL NOT NULL,
                ram_avg REAL NOT NULL, ram_total REAL NOT NULL,
                gpu_avg REAL NOT NULL, gpu_max REAL NOT NULL, vram_avg REAL NOT NULL,
                net_rx INTEGER NOT NULL, net_tx INTEGER NOT NULL, disk_r INTEGER NOT NULL, disk_w INTEGER NOT NULL";

            Exec($@"
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE IF NOT EXISTS apps(
                    app_id INTEGER PRIMARY KEY, exe_path TEXT NOT NULL, name TEXT, publisher TEXT,
                    signed INTEGER NOT NULL DEFAULT 0, sha256 TEXT,
                    first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                    file_size INTEGER, file_mtime INTEGER, identity_json TEXT);
                CREATE TABLE IF NOT EXISTS minute_usage({usageCols}) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS idx_minute_ts ON minute_usage(ts);
                CREATE TABLE IF NOT EXISTS quarter_usage({usageCols}) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS idx_quarter_ts ON quarter_usage(ts);
                CREATE VIEW IF NOT EXISTS usage_all AS SELECT * FROM minute_usage UNION ALL SELECT * FROM quarter_usage;
                CREATE TABLE IF NOT EXISTS system_minute({sysCols});
                CREATE TABLE IF NOT EXISTS system_quarter({sysCols});
                CREATE VIEW IF NOT EXISTS system_all AS SELECT * FROM system_minute UNION ALL SELECT * FROM system_quarter;
                CREATE TABLE IF NOT EXISTS connections(
                    app_id INTEGER NOT NULL, remote_ip TEXT NOT NULL, port INTEGER NOT NULL, proto TEXT NOT NULL,
                    bytes INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                    PRIMARY KEY (app_id, remote_ip, port, proto)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS idx_conn_last ON connections(last_seen);");
        }
    }

    // ---------------------------------------------------------------- writes

    public void WriteMinute(MinuteBatch batch)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR REPLACE INTO system_minute
                    (ts, dur, cpu_avg, cpu_max, ram_avg, ram_total, gpu_avg, gpu_max, vram_avg, net_rx, net_tx, disk_r, disk_w)
                    VALUES ($ts,$dur,$ca,$cm,$ra,$rt,$ga,$gm,$va,$nr,$nt,$dr,$dw)";
                var s = batch.System;
                cmd.Parameters.AddWithValue("$ts", batch.Ts);
                cmd.Parameters.AddWithValue("$dur", batch.Duration);
                cmd.Parameters.AddWithValue("$ca", s.CpuAvg);
                cmd.Parameters.AddWithValue("$cm", s.CpuMax);
                cmd.Parameters.AddWithValue("$ra", s.RamUsedAvg);
                cmd.Parameters.AddWithValue("$rt", s.RamTotal);
                cmd.Parameters.AddWithValue("$ga", s.GpuAvg);
                cmd.Parameters.AddWithValue("$gm", s.GpuMax);
                cmd.Parameters.AddWithValue("$va", s.VramAvg);
                cmd.Parameters.AddWithValue("$nr", s.NetRx);
                cmd.Parameters.AddWithValue("$nt", s.NetTx);
                cmd.Parameters.AddWithValue("$dr", s.DiskRead);
                cmd.Parameters.AddWithValue("$dw", s.DiskWrite);
                cmd.ExecuteNonQuery();
            }

            if (batch.Apps.Count > 0)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR REPLACE INTO minute_usage
                    (app_id, ts, dur, cpu_avg, cpu_max, ram_avg, ram_max, commit_max, gpu_avg, gpu_max, vram_max, net_rx, net_tx, disk_r, disk_w)
                    VALUES ($id,$ts,$dur,$ca,$cm,$ra,$rm,$cx,$ga,$gm,$vm,$nr,$nt,$dr,$dw)";
                var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
                var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
                var pDur = cmd.Parameters.Add("$dur", SqliteType.Real);
                var pCa = cmd.Parameters.Add("$ca", SqliteType.Real);
                var pCm = cmd.Parameters.Add("$cm", SqliteType.Real);
                var pRa = cmd.Parameters.Add("$ra", SqliteType.Real);
                var pRm = cmd.Parameters.Add("$rm", SqliteType.Real);
                var pCx = cmd.Parameters.Add("$cx", SqliteType.Real);
                var pGa = cmd.Parameters.Add("$ga", SqliteType.Real);
                var pGm = cmd.Parameters.Add("$gm", SqliteType.Real);
                var pVm = cmd.Parameters.Add("$vm", SqliteType.Real);
                var pNr = cmd.Parameters.Add("$nr", SqliteType.Integer);
                var pNt = cmd.Parameters.Add("$nt", SqliteType.Integer);
                var pDr = cmd.Parameters.Add("$dr", SqliteType.Integer);
                var pDw = cmd.Parameters.Add("$dw", SqliteType.Integer);
                cmd.Prepare();
                foreach (var a in batch.Apps)
                {
                    pId.Value = a.AppId; pTs.Value = batch.Ts; pDur.Value = batch.Duration;
                    pCa.Value = a.CpuAvg; pCm.Value = a.CpuMax; pRa.Value = a.RamAvg; pRm.Value = a.RamMax; pCx.Value = a.CommitMax;
                    pGa.Value = a.GpuAvg; pGm.Value = a.GpuMax; pVm.Value = a.VramMax;
                    pNr.Value = a.NetRx; pNt.Value = a.NetTx; pDr.Value = a.DiskRead; pDw.Value = a.DiskWrite;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public void UpsertConnections(IReadOnlyList<ConnectionRow> rows)
    {
        if (rows.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO connections (app_id, remote_ip, port, proto, bytes, first_seen, last_seen)
                VALUES ($id,$ip,$port,$proto,$bytes,$first,$last)
                ON CONFLICT(app_id, remote_ip, port, proto) DO UPDATE SET
                    bytes = bytes + excluded.bytes,
                    first_seen = MIN(first_seen, excluded.first_seen),
                    last_seen = MAX(last_seen, excluded.last_seen)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
            var pPort = cmd.Parameters.Add("$port", SqliteType.Integer);
            var pProto = cmd.Parameters.Add("$proto", SqliteType.Text);
            var pBytes = cmd.Parameters.Add("$bytes", SqliteType.Integer);
            var pFirst = cmd.Parameters.Add("$first", SqliteType.Integer);
            var pLast = cmd.Parameters.Add("$last", SqliteType.Integer);
            cmd.Prepare();
            foreach (var r in rows)
            {
                pId.Value = r.AppId; pIp.Value = r.RemoteIp; pPort.Value = r.Port; pProto.Value = r.Proto;
                pBytes.Value = r.Bytes; pFirst.Value = r.FirstSeen; pLast.Value = r.LastSeen;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void UpsertApp(AppIdentity id, long? fileMtimeUnix = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO apps (app_id, exe_path, name, publisher, signed, sha256, first_seen, last_seen, file_size, file_mtime, identity_json)
                VALUES ($id,$path,$name,$pub,$signed,$sha,$first,$last,$size,$mtime,$json)
                ON CONFLICT(app_id) DO UPDATE SET
                    exe_path=excluded.exe_path, name=excluded.name, publisher=excluded.publisher, signed=excluded.signed,
                    sha256=excluded.sha256, last_seen=MAX(last_seen, excluded.last_seen),
                    file_size=excluded.file_size, file_mtime=excluded.file_mtime, identity_json=excluded.identity_json";
            cmd.Parameters.AddWithValue("$id", id.AppId);
            cmd.Parameters.AddWithValue("$path", id.ExePath);
            cmd.Parameters.AddWithValue("$name", (object?)id.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pub", (object?)(id.Company ?? id.InstallPublisher) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$signed", (int)id.Signature);
            cmd.Parameters.AddWithValue("$sha", (object?)id.Sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$first", id.FirstSeenUnix == 0 ? TimeUtil.NowUnix() : id.FirstSeenUnix);
            cmd.Parameters.AddWithValue("$last", id.LastSeenUnix == 0 ? TimeUtil.NowUnix() : id.LastSeenUnix);
            cmd.Parameters.AddWithValue("$size", id.FileSize);
            cmd.Parameters.AddWithValue("$mtime", (object?)fileMtimeUnix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(id, IdentityJson.Options));
            cmd.ExecuteNonQuery();
        }
    }

    public void TouchApp(long appId, long lastSeenUnix)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE apps SET last_seen = MAX(last_seen, $t) WHERE app_id = $id";
            cmd.Parameters.AddWithValue("$t", lastSeenUnix);
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- identity cache

    public sealed record StoredIdentity(AppIdentity Identity, long? FileSize, long? FileMtimeUnix);

    public StoredIdentity? LoadIdentity(long appId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT identity_json, file_size, file_mtime FROM apps WHERE app_id = $id";
            cmd.Parameters.AddWithValue("$id", appId);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return null;
            var id = JsonSerializer.Deserialize<AppIdentity>(r.GetString(0), IdentityJson.Options);
            return id == null ? null : new StoredIdentity(id, r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2));
        }
    }

    // ---------------------------------------------------------------- queries

    public List<AppSummary> GetSummary(long from, long to)
    {
        var list = new List<AppSummary>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                WITH r AS (SELECT * FROM usage_all WHERE ts >= $from AND ts < $to),
                     cov AS (SELECT COALESCE(SUM(d), 0) AS c FROM (SELECT MAX(dur) AS d FROM r GROUP BY ts))
                SELECT r.app_id, a.name, a.exe_path, a.publisher, COALESCE(a.signed, 0),
                       SUM(r.cpu_avg * r.dur) / NULLIF(cov.c, 0), MAX(r.cpu_max),
                       SUM(r.ram_avg * r.dur) / NULLIF(SUM(r.dur), 0), MAX(r.ram_max), MAX(r.commit_max),
                       SUM(r.gpu_avg * r.dur) / NULLIF(cov.c, 0), MAX(r.gpu_max), MAX(r.vram_max),
                       SUM(r.net_rx), SUM(r.net_tx), SUM(r.disk_r), SUM(r.disk_w), MAX(r.ts)
                FROM r CROSS JOIN cov LEFT JOIN apps a ON a.app_id = r.app_id
                GROUP BY r.app_id";
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                list.Add(new AppSummary
                {
                    AppId = rd.GetInt64(0),
                    Name = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    ExePath = rd.IsDBNull(2) ? null : rd.GetString(2),
                    Publisher = rd.IsDBNull(3) ? null : rd.GetString(3),
                    Signature = (SignatureState)rd.GetInt32(4),
                    AvgCpu = D(rd, 5), MaxCpu = D(rd, 6),
                    AvgRam = D(rd, 7), MaxRam = D(rd, 8), MaxCommit = D(rd, 9),
                    AvgGpu = D(rd, 10), MaxGpu = D(rd, 11), MaxVram = D(rd, 12),
                    NetRxBytes = rd.GetInt64(13), NetTxBytes = rd.GetInt64(14),
                    DiskReadBytes = rd.GetInt64(15), DiskWriteBytes = rd.GetInt64(16),
                    LastSeenUnix = rd.GetInt64(17),
                });
            }
        }
        return list;
    }

    private static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    /// <summary>Time series bucketed to <paramref name="bucketSeconds"/>; appId null = whole system.</summary>
    public List<SeriesPoint> GetSeries(long from, long to, int bucketSeconds, long? appId)
    {
        if (bucketSeconds < 1) bucketSeconds = 60;
        var list = new List<SeriesPoint>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (appId == null)
            {
                cmd.CommandText = @"
                    SELECT (ts / $b) * $b AS bucket,
                           AVG(cpu_avg), MAX(cpu_max), AVG(ram_avg), AVG(gpu_avg), MAX(gpu_max), AVG(vram_avg),
                           AVG(net_rx / dur), AVG(net_tx / dur), AVG(disk_r / dur), AVG(disk_w / dur)
                    FROM system_all WHERE ts >= $from AND ts < $to AND dur > 0
                    GROUP BY bucket ORDER BY bucket";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT (ts / $b) * $b AS bucket,
                           AVG(cpu_avg), MAX(cpu_max), AVG(ram_avg), AVG(gpu_avg), MAX(gpu_max), AVG(vram_max),
                           AVG(net_rx / dur), AVG(net_tx / dur), AVG(disk_r / dur), AVG(disk_w / dur)
                    FROM usage_all WHERE ts >= $from AND ts < $to AND app_id = $app AND dur > 0
                    GROUP BY bucket ORDER BY bucket";
                cmd.Parameters.AddWithValue("$app", appId.Value);
            }
            cmd.Parameters.AddWithValue("$b", bucketSeconds);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                list.Add(new SeriesPoint
                {
                    Ts = rd.GetInt64(0),
                    Cpu = D(rd, 1), CpuMax = D(rd, 2), Ram = D(rd, 3), Gpu = D(rd, 4), GpuMax = D(rd, 5), Vram = D(rd, 6),
                    NetRx = D(rd, 7), NetTx = D(rd, 8), DiskRead = D(rd, 9), DiskWrite = D(rd, 10),
                });
            }
        }
        return list;
    }

    public List<ConnectionInfo> GetConnections(long appId, int limit)
    {
        var list = new List<ConnectionInfo>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"SELECT remote_ip, port, proto, bytes, first_seen, last_seen FROM connections
                WHERE app_id = $id ORDER BY last_seen DESC, bytes DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$id", appId);
            cmd.Parameters.AddWithValue("$n", limit);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new ConnectionInfo
                {
                    RemoteIp = rd.GetString(0), Port = rd.GetInt32(1), Proto = rd.GetString(2), Bytes = rd.GetInt64(3),
                    FirstSeenUnix = rd.GetInt64(4), LastSeenUnix = rd.GetInt64(5),
                });
        }
        return list;
    }

    public long CountRows(string table)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    // ---------------------------------------------------------------- retention

    public RetentionResult RunRetention(long nowUnix)
    {
        lock (_gate)
        {
            long cut = (nowUnix - MinuteRetentionDays * 86400L) / QuarterSeconds * QuarterSeconds;
            int rolled, quarterDeleted, connDeleted, appsDeleted;

            using var tx = _db.BeginTransaction();

            // Per-app rows: bucket coverage = distinct system minutes in the bucket, so absent minutes count as idle.
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO quarter_usage
                    SELECT m.app_id, (m.ts / 900) * 900, c.cov,
                           SUM(m.cpu_avg * m.dur) / c.cov, MAX(m.cpu_max),
                           SUM(m.ram_avg * m.dur) / SUM(m.dur), MAX(m.ram_max), MAX(m.commit_max),
                           SUM(m.gpu_avg * m.dur) / c.cov, MAX(m.gpu_max), MAX(m.vram_max),
                           SUM(m.net_rx), SUM(m.net_tx), SUM(m.disk_r), SUM(m.disk_w)
                    FROM minute_usage m
                    JOIN (SELECT q, SUM(d) AS cov
                          FROM (SELECT (ts / 900) * 900 AS q, ts, MAX(dur) AS d FROM minute_usage WHERE ts < $cut GROUP BY ts)
                          GROUP BY q) c ON c.q = (m.ts / 900) * 900
                    WHERE m.ts < $cut
                    GROUP BY m.app_id, (m.ts / 900) * 900";
                cmd.Parameters.AddWithValue("$cut", cut);
                rolled = cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO system_quarter
                    SELECT (ts / 900) * 900, SUM(dur),
                           SUM(cpu_avg * dur) / SUM(dur), MAX(cpu_max),
                           SUM(ram_avg * dur) / SUM(dur), MAX(ram_total),
                           SUM(gpu_avg * dur) / SUM(dur), MAX(gpu_max), SUM(vram_avg * dur) / SUM(dur),
                           SUM(net_rx), SUM(net_tx), SUM(disk_r), SUM(disk_w)
                    FROM system_minute WHERE ts < $cut AND dur > 0
                    GROUP BY (ts / 900) * 900";
                cmd.Parameters.AddWithValue("$cut", cut);
                cmd.ExecuteNonQuery();
            }
            ExecIn(tx, "DELETE FROM minute_usage WHERE ts < $cut", ("$cut", cut));
            ExecIn(tx, "DELETE FROM system_minute WHERE ts < $cut", ("$cut", cut));

            long dropBefore = nowUnix - QuarterRetentionDays * 86400L;
            quarterDeleted = ExecIn(tx, "DELETE FROM quarter_usage WHERE ts < $t", ("$t", dropBefore));
            ExecIn(tx, "DELETE FROM system_quarter WHERE ts < $t", ("$t", dropBefore));
            connDeleted = ExecIn(tx, "DELETE FROM connections WHERE last_seen < $t", ("$t", dropBefore));
            appsDeleted = ExecIn(tx, @"DELETE FROM apps WHERE last_seen < $t
                AND app_id NOT IN (SELECT app_id FROM minute_usage) AND app_id NOT IN (SELECT app_id FROM quarter_usage)",
                ("$t", dropBefore));
            tx.Commit();

            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            Exec("PRAGMA incremental_vacuum;");
            Exec("PRAGMA optimize;");
            return new RetentionResult(rolled, quarterDeleted, connDeleted, appsDeleted);
        }
    }

    private int ExecIn(SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* best effort */ }
            _db.Dispose();
        }
    }
}

internal static class IdentityJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
