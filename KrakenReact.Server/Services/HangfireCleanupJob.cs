using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>
/// Removes Hangfire's record of long-past failed jobs.
/// <para>
/// Hangfire expires succeeded jobs after a day but keeps failed ones forever, deliberately, so that
/// a failure can still be inspected. When a job fails persistently that guarantee turns into
/// unbounded growth: on this database 5,598 failed jobs had accumulated over four months, none of
/// them expiring, against 3,572 succeeded ones representing a single day.
/// </para>
/// <para>
/// It is worth being clear about what this does and does not fix. Those failures were almost all SQL
/// execution timeouts, and deleting the evidence does not make the timeouts stop. What it does stop
/// is the feedback loop, where every timeout writes rows that are never reclaimed, growing the
/// tables that the contended server is already struggling to keep up with. Recent failures are kept
/// so the cause stays diagnosable.
/// </para>
/// </summary>
public class HangfireCleanupJob
{
    /// <summary>Rows removed per statement, to keep each delete's lock footprint small.</summary>
    private const int BatchSize = 2000;

    /// <summary>Statements per run, so one pass cannot monopolise a struggling server.</summary>
    private const int MaxBatchesPerRun = 25;

    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<HangfireCleanupJob> _logger;

    public HangfireCleanupJob(IDbContextFactory<KrakenDbContext> dbFactory, ILogger<HangfireCleanupJob> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        int retentionDays;
        await using (var settingsDb = await _dbFactory.CreateDbContextAsync(ct))
        {
            var setting = await settingsDb.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == "HangfireFailedJobRetentionDays", ct);
            retentionDays = setting is not null && int.TryParse(setting.Value, out int parsed)
                ? Math.Clamp(parsed, 1, 365)
                : 14;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        int totalDeleted = 0;

        for (int batch = 0; batch < MaxBatchesPerRun && !ct.IsCancellationRequested; batch++)
        {
            int deleted;
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                // State and JobParameter both cascade from Job, so deleting the parent is enough.
                deleted = await db.Database.ExecuteSqlRawAsync(
                    @"DELETE TOP (@p0) FROM [HangFire].[Job]
                      WHERE StateName = 'Failed' AND CreatedAt < @p1",
                    [BatchSize, cutoff], ct);
            }
            catch (Exception ex)
            {
                // A contended server is exactly the condition this job exists for, so a timeout here
                // is expected occasionally. Stop quietly and let the next run continue.
                _logger.LogWarning(ex, "[HangfireCleanup] Stopped after {Deleted} rows", totalDeleted);
                return;
            }

            totalDeleted += deleted;
            if (deleted < BatchSize) break;
        }

        if (totalDeleted > 0)
            _logger.LogInformation("[HangfireCleanup] Removed {Count} failed job records older than {Days} days",
                totalDeleted, retentionDays);
    }
}
