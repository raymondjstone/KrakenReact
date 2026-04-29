using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class DrawdownAlertJob
{
    private readonly TradingStateService _state;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly NotificationService _notify;
    private readonly ILogger<DrawdownAlertJob> _logger;

    public DrawdownAlertJob(TradingStateService state, IDbContextFactory<KrakenDbContext> dbFactory, NotificationService notify, ILogger<DrawdownAlertJob> logger)
    {
        _state = state;
        _dbFactory = dbFactory;
        _notify = notify;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (!_state.DrawdownAlertEnabled) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow.Date.AddDays(-90);
        var snapshots = await db.PortfolioSnapshots
            .Where(s => s.Date >= since)
            .OrderBy(s => s.Date)
            .Select(s => new { s.Date, s.TotalUsd })
            .ToListAsync(ct);

        if (snapshots.Count < 5) return;

        var values = snapshots.Select(s => (double)s.TotalUsd).ToArray();
        double peak = values[0], maxDd = 0;
        double troughValue = values[0];
        DateTime peakDate = snapshots[0].Date, troughDate = snapshots[0].Date;

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > peak)
            {
                peak = values[i];
                peakDate = snapshots[i].Date;
            }
            var dd = peak > 0 ? (peak - values[i]) / peak * 100 : 0;
            if (dd > maxDd)
            {
                maxDd = dd;
                troughValue = values[i];
                troughDate = snapshots[i].Date;
            }
        }

        _logger.LogInformation("[DrawdownAlert] Current max drawdown: {Dd:F1}% (threshold {T:F1}%)", maxDd, (double)_state.DrawdownAlertThreshold);

        if (maxDd >= (double)_state.DrawdownAlertThreshold)
        {
            await _notify.Pushover(
                $"Portfolio Drawdown Alert — {maxDd:F1}%",
                $"Portfolio has drawn down {maxDd:F1}% from peak of ${peak:N0} on {peakDate:yyyy-MM-dd} to ${troughValue:N0} on {troughDate:yyyy-MM-dd}. Threshold: {_state.DrawdownAlertThreshold:F1}%.");
        }
    }
}
