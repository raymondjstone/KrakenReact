using Hangfire;
using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class MultiTfPredictionJob
{
    private readonly PredictionJob _predictionJob;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<MultiTfPredictionJob> _logger;

    // Default intervals to evaluate when no override is specified
    private static readonly string[] DefaultIntervals = ["OneHour", "FourHour", "OneDay"];

    public MultiTfPredictionJob(
        PredictionJob predictionJob,
        IDbContextFactory<KrakenDbContext> dbFactory,
        ILogger<MultiTfPredictionJob> logger)
    {
        _predictionJob = predictionJob;
        _dbFactory     = dbFactory;
        _logger        = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExecuteAsync(string symbol, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "MultiTfIntervals", ct);
        var intervals = setting != null
            ? setting.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray()
            : DefaultIntervals;

        _logger.LogInformation("[MultiTf] Running {Symbol} across {Count} intervals", symbol, intervals.Length);

        foreach (var intervalName in intervals)
        {
            if (ct.IsCancellationRequested) break;
            await _predictionJob.ExecuteMultiTfAsync(symbol, intervalName, ct);
            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[MultiTf] Complete for {Symbol}", symbol);
    }
}
