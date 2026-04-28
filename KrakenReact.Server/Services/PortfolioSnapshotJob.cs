using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class PortfolioSnapshotJob
{
    private readonly TradingStateService _state;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<PortfolioSnapshotJob> _logger;

    public PortfolioSnapshotJob(TradingStateService state, IDbContextFactory<KrakenDbContext> dbFactory, ILogger<PortfolioSnapshotJob> logger)
    {
        _state = state;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var totalUsd = _state.Balances.Values.Sum(b => b.LatestValue);
            var totalGbp = _state.Balances.Values.Sum(b => b.LatestValueGbp);
            var today = DateTime.UtcNow.Date;

            var existing = await db.PortfolioSnapshots.FindAsync(today);
            if (existing != null)
            {
                existing.TotalUsd = totalUsd;
                existing.TotalGbp = totalGbp;
            }
            else
            {
                db.PortfolioSnapshots.Add(new PortfolioSnapshot
                {
                    Date = today,
                    TotalUsd = totalUsd,
                    TotalGbp = totalGbp,
                });
            }
            await db.SaveChangesAsync();
            _logger.LogInformation("[PortfolioSnapshot] Saved ${TotalUsd:F2} / £{TotalGbp:F2}", totalUsd, totalGbp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PortfolioSnapshot] Snapshot failed");
        }
    }
}
