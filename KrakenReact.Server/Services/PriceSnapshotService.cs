using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class PriceSnapshotService : BackgroundService
{
    private readonly TradingStateService _state;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<PriceSnapshotService> _logger;

    public PriceSnapshotService(TradingStateService state, IDbContextFactory<KrakenDbContext> dbFactory, ILogger<PriceSnapshotService> logger)
    {
        _state = state;
        _dbFactory = dbFactory;
        _logger = logger;
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
        try
        {
            var now = DateTime.UtcNow;
            var snapshots = _state.Prices
                .Select(kvp => new { kvp.Key, Price = kvp.Value.BestKline?.Close ?? 0 })
                .Where(x => x.Price > 0)
                .Select(x => new PriceSnapshot { Symbol = x.Key, Price = x.Price, CapturedAt = now })
                .ToList();

            if (snapshots.Count == 0) return;

            await using var db = await _dbFactory.CreateDbContextAsync();
            db.PriceSnapshots.AddRange(snapshots);

            // Delete snapshots older than 26 hours to keep the table small
            var cutoff = now.AddHours(-26);
            await db.PriceSnapshots.Where(s => s.CapturedAt < cutoff).ExecuteDeleteAsync();

            await db.SaveChangesAsync();
            _logger.LogDebug("[PriceSnapshot] Captured {Count} snapshots at {Time}", snapshots.Count, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceSnapshot] Error capturing price snapshots");
        }
    }
}
