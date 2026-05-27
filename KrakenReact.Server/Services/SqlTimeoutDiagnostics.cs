using Microsoft.Data.SqlClient;

namespace KrakenReact.Server.Services;

/// <summary>
/// Captures a snapshot of SQL Server activity (active requests, locks, head blockers)
/// when a query times out (SqlException.Number == -2). Uses a fresh side-connection
/// with a tiny timeout so the diagnostic itself cannot pile onto the problem it is
/// diagnosing, and self-throttles to one report per minute.
/// </summary>
public sealed class SqlTimeoutDiagnostics
{
    private readonly string _connStr;
    private readonly ILogger<SqlTimeoutDiagnostics> _logger;
    private static long _lastReportTicks;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    public SqlTimeoutDiagnostics(IConfiguration cfg, ILogger<SqlTimeoutDiagnostics> logger)
    {
        _connStr = cfg.GetConnectionString("EFDB")
            ?? throw new InvalidOperationException("EFDB connection string missing");
        _logger = logger;
    }

    public static bool IsSqlTimeout(Exception ex)
    {
        for (var e = (Exception?)ex; e != null; e = e.InnerException)
        {
            if (e is SqlException se && se.Number == -2) return true;
        }
        return false;
    }

    /// <summary>Fire-and-forget diagnostic snapshot. Safe to call from a catch block.</summary>
    public void CaptureIfTimeout(string context, Exception triggeringEx)
    {
        if (!IsSqlTimeout(triggeringEx)) return;

        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastReportTicks);
        if (lastTicks != 0 && new TimeSpan(nowTicks - lastTicks) < MinInterval) return;
        Interlocked.Exchange(ref _lastReportTicks, nowTicks);

        _ = Task.Run(() => CaptureInternalAsync(context));
    }

    private async Task CaptureInternalAsync(string context)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(_connStr)
            {
                ConnectTimeout = 5,
                ApplicationName = "KrakenReact-Diag",
                Pooling = false
            };

            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();

            _logger.LogWarning("[SqlDiag/{Context}] === diagnostic snapshot start ===", context);
            await DumpActiveRequestsAsync(conn, context);
            await DumpHeadBlockersAsync(conn, context);
            await DumpLockSummaryAsync(conn, context);
            await DumpServerHealthAsync(conn, context);
            _logger.LogWarning("[SqlDiag/{Context}] === diagnostic snapshot end ===", context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SqlDiag/{Context}] Snapshot itself failed (DB may be unreachable)", context);
        }
    }

    private async Task DumpActiveRequestsAsync(SqlConnection conn, string context)
    {
        const string sql = @"
SELECT TOP 10
    r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, r.wait_resource,
    r.status, r.command, r.cpu_time, r.total_elapsed_time,
    DB_NAME(r.database_id) AS db_name,
    SUBSTRING(t.text, (r.statement_start_offset/2)+1,
        ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2) + 1) AS stmt
FROM sys.dm_exec_requests r
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.session_id <> @@SPID AND r.session_id > 50
ORDER BY r.total_elapsed_time DESC";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 5 };
        await using var rdr = await cmd.ExecuteReaderAsync();
        int rows = 0;
        while (await rdr.ReadAsync())
        {
            rows++;
            _logger.LogWarning(
                "[SqlDiag/{Context}] active sess={Sess} blockedBy={Block} wait={Wait}({WaitMs}ms) res={Res} cmd={Cmd} elapsed={Elapsed}ms db={Db} stmt={Stmt}",
                context,
                rdr["session_id"], rdr["blocking_session_id"], rdr["wait_type"],
                rdr["wait_time"], rdr["wait_resource"], rdr["command"],
                rdr["total_elapsed_time"], rdr["db_name"], Truncate(rdr["stmt"] as string, 300));
        }
        if (rows == 0) _logger.LogWarning("[SqlDiag/{Context}] no active user requests", context);
    }

    private async Task DumpHeadBlockersAsync(SqlConnection conn, string context)
    {
        const string sql = @"
SELECT
    s.session_id, s.login_name, s.host_name, s.program_name, s.status,
    s.last_request_start_time, s.cpu_time, s.memory_usage, s.open_transaction_count,
    (SELECT TOP 1 SUBSTRING(t.text, (r.statement_start_offset/2)+1,
        ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2) + 1)
     FROM sys.dm_exec_requests r OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
     WHERE r.session_id = s.session_id) AS current_stmt
FROM sys.dm_exec_sessions s
WHERE s.session_id IN (SELECT DISTINCT blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0)";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 5 };
        await using var rdr = await cmd.ExecuteReaderAsync();
        int rows = 0;
        while (await rdr.ReadAsync())
        {
            rows++;
            _logger.LogWarning(
                "[SqlDiag/{Context}] HEAD BLOCKER sess={Sess} login={Login} host={Host} prog={Prog} status={Status} openTx={Tx} cpu={Cpu} stmt={Stmt}",
                context,
                rdr["session_id"], rdr["login_name"], rdr["host_name"], rdr["program_name"],
                rdr["status"], rdr["open_transaction_count"], rdr["cpu_time"],
                Truncate(rdr["current_stmt"] as string, 300));
        }
        if (rows == 0) _logger.LogWarning("[SqlDiag/{Context}] no head blockers found", context);
    }

    private async Task DumpLockSummaryAsync(SqlConnection conn, string context)
    {
        const string sql = @"
SELECT TOP 10
    request_session_id AS sess,
    SUM(CASE WHEN request_status = 'WAIT' THEN 1 ELSE 0 END) AS waiting_locks,
    SUM(CASE WHEN request_status = 'GRANT' THEN 1 ELSE 0 END) AS granted_locks,
    SUM(CASE WHEN request_mode IN ('X','IX','SCH-M','BU') THEN 1 ELSE 0 END) AS heavy_locks
FROM sys.dm_tran_locks
GROUP BY request_session_id
HAVING SUM(CASE WHEN request_status = 'WAIT' THEN 1 ELSE 0 END) > 0
    OR SUM(CASE WHEN request_mode IN ('X','SCH-M') THEN 1 ELSE 0 END) > 0
ORDER BY waiting_locks DESC, heavy_locks DESC";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 5 };
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            _logger.LogWarning(
                "[SqlDiag/{Context}] locks sess={Sess} waiting={Wait} granted={Grant} heavy(X/IX/Sch-M/BU)={Heavy}",
                context, rdr["sess"], rdr["waiting_locks"], rdr["granted_locks"], rdr["heavy_locks"]);
        }
    }

    private async Task DumpServerHealthAsync(SqlConnection conn, string context)
    {
        const string sql = @"
SELECT
    (SELECT COUNT(*) FROM sys.dm_exec_connections)             AS conn_count,
    (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1) AS user_sessions,
    (SELECT COUNT(*) FROM sys.dm_os_waiting_tasks)             AS waiting_tasks,
    (SELECT TOP 1 wait_type FROM sys.dm_os_wait_stats
       WHERE wait_type NOT IN ('SLEEP_TASK','BROKER_TASK_STOP','SQLTRACE_BUFFER_FLUSH',
             'CLR_AUTO_EVENT','CLR_MANUAL_EVENT','LAZYWRITER_SLEEP','CHECKPOINT_QUEUE',
             'REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT','BROKER_TO_FLUSH',
             'BROKER_RECEIVE_WAITFOR','HADR_FILESTREAM_IOMGR_IOCOMPLETION','DIRTY_PAGE_POLL',
             'WAITFOR','BROKER_EVENTHANDLER','TRACEWRITE','FT_IFTSHC_MUTEX',
             'DISPATCHER_QUEUE_SEMAPHORE','PREEMPTIVE_OS_AUTHENTICATIONOPS')
       ORDER BY waiting_tasks_count DESC) AS top_wait,
    CAST((SELECT cntr_value FROM sys.dm_os_performance_counters
       WHERE counter_name = 'Page life expectancy' AND object_name LIKE '%Buffer Manager%') AS bigint) AS ple_sec";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 5 };
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (await rdr.ReadAsync())
        {
            _logger.LogWarning(
                "[SqlDiag/{Context}] health conns={Conns} userSess={Users} waitingTasks={Wait} topWait={TopWait} PLE={Ple}s",
                context,
                rdr["conn_count"], rdr["user_sessions"], rdr["waiting_tasks"],
                rdr["top_wait"], rdr["ple_sec"]);
        }
    }

    private static string? Truncate(string? s, int max)
    {
        if (s == null) return null;
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
