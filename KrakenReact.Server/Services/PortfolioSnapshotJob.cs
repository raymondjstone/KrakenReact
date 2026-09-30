using Hangfire;
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

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync()
    {
        try
        {
            // The value comes from in-memory balances and live prices. Right after a restart those can be empty, and with the
            // price feed down they are stale; recording either as "today's value" overwrote a good snapshot with 0 (or an old
            // figure), which the drawdown alert then read as the portfolio collapsing.
            if (!_state.IsPriceFeedAlive())
            {
                _logger.LogWarning("[PortfolioSnapshot] Skipped — the live price feed is not delivering, so today's value is unknown");
                return;
            }

            var totalUsd = _state.Balances.Values.Sum(b => b.LatestValue);
            var totalGbp = _state.Balances.Values.Sum(b => b.LatestValueGbp);
            if (totalUsd <= 0)
            {
                _logger.LogWarning("[PortfolioSnapshot] Skipped — the portfolio value is zero (balances not loaded yet?)");
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
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
