using System.Data;
using System.Diagnostics;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class PriceSnapshotService : BackgroundService
{
    private readonly TradingStateService _state;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<PriceSnapshotService> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;

    // Skip the next capture if the previous one took longer than this — when SQL Server's
    // disk is saturated (PREEMPTIVE_OS_FLUSHFILEBUFFERS waits), piling on another insert
    // makes the cascade worse. Losing one 5-min tick of price snapshots is harmless.
    private static readonly TimeSpan SlowWriteThreshold = TimeSpan.FromSeconds(5);
    private TimeSpan _lastInsertDuration = TimeSpan.Zero;
    private int _tickCounter;

    public PriceSnapshotService(
        TradingStateService state,
        IDbContextFactory<KrakenDbContext> dbFactory,
        ILogger<PriceSnapshotService> logger,
        SqlTimeoutDiagnostics sqlDiag)
    {
        _state = state;
        _dbFactory = dbFactory;
        _logger = logger;
        _sqlDiag = sqlDiag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Align first capture to the next 5-minute boundary
        var now = DateTime.UtcNow;
        var minutesUntilNext = 5 - (now.Minute % 5);
        var secondsUntilNext = minutesUntilNext * 60 - now.Second;
        await Task.Delay(TimeSpan.FromSeconds(secondsUntilNext), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CaptureSnapshotAsync();
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CaptureSnapshotAsync()
    {
        if (_lastInsertDuration > SlowWriteThreshold)
        {
            _logger.LogWarning(
                "[PriceSnapshot] Skipping capture — last write took {Ms}ms (>{Threshold}ms); SQL disk may be saturated",
                (int)_lastInsertDuration.TotalMilliseconds, (int)SlowWriteThreshold.TotalMilliseconds);
            _lastInsertDuration = TimeSpan.Zero; // try again next tick
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var snapshots = _state.Prices
                .Select(kvp => new { kvp.Key, Price = kvp.Value.BestKline?.Close ?? 0 })
                .Where(x => x.Price > 0)
                .Select(x => new PriceSnapshot { Symbol = x.Key, Price = x.Price, CapturedAt = now })
                .ToList();

            if (snapshots.Count == 0) return;

            var sw = Stopwatch.StartNew();
            await BulkInsertAsync(snapshots);
            sw.Stop();
            _lastInsertDuration = sw.Elapsed;

            _tickCounter++;
            // Run the 26-hour retention DELETE once an hour (every 12 ticks) instead of every
            // 5 min. Cuts cleanup-driven log writes by 12x; data is still trimmed daily.
            if (_tickCounter % 12 == 0)
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(30));
                var cutoff = now.AddHours(-26);
                var deleted = await db.PriceSnapshots.Where(s => s.CapturedAt < cutoff).ExecuteDeleteAsync();
                _logger.LogDebug("[PriceSnapshot] Hourly cleanup removed {N} rows older than {Cutoff:O}", deleted, cutoff);
            }

            _logger.LogDebug("[PriceSnapshot] Captured {Count} snapshots in {Ms}ms", snapshots.Count, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceSnapshot] Error capturing price snapshots");
            _sqlDiag.CaptureIfTimeout("PriceSnapshotService.Capture", ex);
        }
    }

    /// <summary>
    /// SqlBulkCopy keeps the log footprint and lock count far smaller than a parameterized
    /// VALUES INSERT. Important because the EF INSERT was acquiring ~490 heavy locks per
    /// capture and waiting on PREEMPTIVE_OS_FLUSHFILEBUFFERS for 10-17 s on a saturated disk.
    /// </summary>
    private async Task BulkInsertAsync(List<PriceSnapshot> snapshots)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var connStr = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("No connection string");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        using var table = new DataTable();
        table.Columns.Add("Symbol", typeof(string));
        table.Columns.Add("Price", typeof(decimal));
        table.Columns.Add("CapturedAt", typeof(DateTime));
        foreach (var s in snapshots)
            table.Rows.Add(s.Symbol, s.Price, s.CapturedAt);

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "PriceSnapshots",
            BatchSize = 200,
            BulkCopyTimeout = 30,
        };
        bulk.ColumnMappings.Add("Symbol", "Symbol");
        bulk.ColumnMappings.Add("Price", "Price");
        bulk.ColumnMappings.Add("CapturedAt", "CapturedAt");
        await bulk.WriteToServerAsync(table);
    }
}
