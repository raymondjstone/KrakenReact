using Hangfire;
using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class StalePredictionRefreshJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly PredictionJob _predictionJob;
    private readonly ILogger<StalePredictionRefreshJob> _logger;

    public const int StalenessThresholdMinutes = 30;

    public StalePredictionRefreshJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        PredictionJob predictionJob,
        ILogger<StalePredictionRefreshJob> logger)
    {
        _dbFactory = dbFactory;
        _predictionJob = predictionJob;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow.AddMinutes(-StalenessThresholdMinutes);

        var staleSymbols = await db.PredictionResults
            .AsNoTracking()
            .Where(r => r.ComputedAt < cutoff)
            .Select(r => r.Symbol)
            .OrderBy(r => r)
            .ToListAsync(ct);

        if (staleSymbols.Count == 0)
        {
            _logger.LogInformation("[StalePredictJob] No predictions older than {Mins} min", StalenessThresholdMinutes);
            return;
        }

        _logger.LogInformation("[StalePredictJob] Refreshing {Count} stale prediction(s)", staleSymbols.Count);

        foreach (var symbol in staleSymbols)
        {
            if (ct.IsCancellationRequested) break;
            await _predictionJob.ExecuteSingleAsync(symbol, ct);
            try { await Task.Delay(600, ct); } catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[StalePredictJob] Done");
    }
}
