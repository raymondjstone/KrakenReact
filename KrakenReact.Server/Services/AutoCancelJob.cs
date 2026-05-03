using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class AutoCancelJob
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly NotificationService _notify;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<AutoCancelJob> _logger;

    public AutoCancelJob(TradingStateService state, KrakenRestService kraken, NotificationService notify,
        IDbContextFactory<KrakenDbContext> dbFactory, ILogger<AutoCancelJob> logger)
    {
        _state = state;
        _kraken = kraken;
        _notify = notify;
        _dbFactory = dbFactory;
        _logger = logger;
    }

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

        foreach (var order in candidates)
        {
            try
            {
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
}
