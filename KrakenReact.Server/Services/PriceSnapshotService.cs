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

            // Raw INSERT avoids the EF Core MERGE that auto-increment PKs force.
            // MERGE acquires UPDATE locks on the whole table; plain INSERT does not.
            var sb = new System.Text.StringBuilder("INSERT INTO [PriceSnapshots] ([Symbol],[Price],[CapturedAt]) VALUES ");
            var parms = new List<object>();
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"({{{i * 3}}},{{{i * 3 + 1}}},{{{i * 3 + 2}}})");
                parms.Add(snapshots[i].Symbol);
                parms.Add(snapshots[i].Price);
                parms.Add(snapshots[i].CapturedAt);
            }
            await db.Database.ExecuteSqlRawAsync(sb.ToString(), parms.ToArray());

            // Delete snapshots older than 26 hours (runs after the insert commits)
            var cutoff = now.AddHours(-26);
            await db.PriceSnapshots.Where(s => s.CapturedAt < cutoff).ExecuteDeleteAsync();
            _logger.LogDebug("[PriceSnapshot] Captured {Count} snapshots at {Time}", snapshots.Count, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceSnapshot] Error capturing price snapshots");
        }
    }
}
