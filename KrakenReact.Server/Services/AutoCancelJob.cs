using Hangfire;
using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class AutoCancelJob
{
    private readonly TradingStateService _state;
    private readonly IOrderGateway _kraken;
    private readonly INotifier _notify;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<AutoCancelJob> _logger;

    public AutoCancelJob(TradingStateService state, IOrderGateway kraken, INotifier notify,
        IDbContextFactory<KrakenDbContext> dbFactory, ILogger<AutoCancelJob> logger)
    {
        _state = state;
        _kraken = kraken;
        _notify = notify;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (!_state.AutoCancelEnabled) return;
        if (!_state.AutoCancelBuys && !_state.AutoCancelSells) return;

        var cutoff = DateTime.UtcNow.AddDays(-_state.AutoCancelDays);
        var candidates = _state.Orders.Values
            .Where(o => TradingStateService.IsOpenOrderStatus(o.Status) && o.CreateTime < cutoff)
            .Where(o => (o.Side == "Buy" && _state.AutoCancelBuys) || (o.Side == "Sell" && _state.AutoCancelSells))
            .ToList();

        if (!candidates.Any()) return;

        // Orders another feature is actively managing are not "stale": a MicroTrade sell waiting for its target, or a bracket's
        // take-profit, can legitimately rest for months. Cancelling one silently strips that position of its exit.
        var managed = await GetManagedOrderIdsAsync(ct);

        foreach (var order in candidates)
        {
            try
            {
                if (managed.Contains(order.Id))
                {
                    _logger.LogInformation("[AutoCancel] Leaving {OrderId} alone — it belongs to a MicroTrade position or bracket", order.Id);
                    continue;
                }

                var age = (int)(DateTime.UtcNow - order.CreateTime).TotalDays;
                var label = $"{order.Side} {order.Quantity} {order.Symbol} @ {order.Price:F4} (age: {age}d)";

                if (_state.DryRunJobs)
                {
                    await _notify.Pushover($"DRY RUN — would cancel order", label);
                    _logger.LogInformation("[AutoCancel] DRY RUN — would cancel {OrderId} {Label}", order.Id, label);
                    continue;
                }

                var ok = await _kraken.CancelOrderAsync(order.Id);
                if (ok)
                {
                    await _notify.Pushover("Auto-cancelled stale order", label);
                    _logger.LogInformation("[AutoCancel] Cancelled {OrderId} {Label}", order.Id, label);
                }
                else
                {
                    _logger.LogWarning("[AutoCancel] Failed to cancel {OrderId}", order.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AutoCancel] Exception cancelling {OrderId}", order.Id);
            }
        }
    }

    /// <summary>Kraken order ids currently owned by an open MicroTrade position or an unfinished bracket.</summary>
    private async Task<HashSet<string>> GetManagedOrderIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var micro = await db.MicroTradeOrders.AsNoTracking()
            .Where(o => o.Status == "Buying" || o.Status == "Selling" || o.Status == "Placing")
            .Select(o => new { o.BuyOrderId, o.SellOrderId })
            .ToListAsync(ct);
        foreach (var o in micro)
        {
            if (!string.IsNullOrEmpty(o.BuyOrderId)) ids.Add(o.BuyOrderId);
            if (!string.IsNullOrEmpty(o.SellOrderId)) ids.Add(o.SellOrderId);
        }

        var brackets = await db.BracketOrders.AsNoTracking()
            .Where(b => b.Status == "Watching" || b.Status == "Active")
            .Select(b => new { b.KrakenOrderId, b.TakeProfitOrderId, b.StopOrderId })
            .ToListAsync(ct);
        foreach (var b in brackets)
        {
            if (!string.IsNullOrEmpty(b.KrakenOrderId)) ids.Add(b.KrakenOrderId);
            if (!string.IsNullOrEmpty(b.TakeProfitOrderId)) ids.Add(b.TakeProfitOrderId);
            if (!string.IsNullOrEmpty(b.StopOrderId)) ids.Add(b.StopOrderId);
        }

        return ids;
    }
}
