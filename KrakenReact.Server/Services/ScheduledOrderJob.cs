using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class ScheduledOrderJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly NotificationService _notify;
    private readonly ILogger<ScheduledOrderJob> _logger;

    public ScheduledOrderJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        KrakenRestService kraken,
        TradingStateService state,
        NotificationService notify,
        ILogger<ScheduledOrderJob> logger)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _notify = notify;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pending = await db.ScheduledOrders
            .Where(o => o.Status == "Pending" && o.ScheduledAt <= DateTime.UtcNow)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        _logger.LogInformation("[ScheduledOrders] Processing {Count} pending order(s)", pending.Count);

        foreach (var order in pending)
        {
            try
            {
                if (_state.DryRunJobs)
                {
                    order.Status = "Executed";
                    order.ExecutedAt = DateTime.UtcNow;
                    _logger.LogInformation("[ScheduledOrders] DRY RUN — would place {Side} {Symbol} {Qty} @ {Price}",
                        order.Side, order.Symbol, order.Quantity, order.Price);
                    await _notify.Pushover(
                        $"DRY RUN — Scheduled {order.Side} {order.Symbol}",
                        $"Would place {order.Side} {order.Quantity} {order.Symbol} @ {order.Price:F4}");
                    continue;
                }

                var side = order.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase)
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                var clientId = $"sched-{order.Id}-{DateTime.UtcNow:yyyyMMddHHmm}";
                var result = await _kraken.PlaceOrderAsync(
                    order.Symbol, side, OrderType.Limit,
                    order.Quantity, order.Price, clientId);

                if (result.Success)
                {
                    order.Status = "Executed";
                    order.ExecutedAt = DateTime.UtcNow;
                    _logger.LogInformation("[ScheduledOrders] Order {Id} executed: {Side} {Symbol} {Qty} @ {Price}",
                        order.Id, order.Side, order.Symbol, order.Quantity, order.Price);
                    await _notify.Pushover(
                        $"Scheduled {order.Side} Executed — {order.Symbol}",
                        $"{order.Side} {order.Quantity} {order.Symbol} @ {order.Price:F4} (order id: {result.Data?.OrderIds?.FirstOrDefault()})");
                }
                else
                {
                    order.Status = "Failed";
                    order.ErrorMessage = result.Error?.Message ?? "Unknown error";
                    _logger.LogError("[ScheduledOrders] Order {Id} failed: {Error}", order.Id, order.ErrorMessage);
                    await _notify.Pushover(
                        $"Scheduled Order Failed — {order.Symbol}",
                        $"Failed to place {order.Side} {order.Quantity} {order.Symbol}: {order.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                order.Status = "Failed";
                order.ErrorMessage = ex.Message;
                _logger.LogError(ex, "[ScheduledOrders] Exception processing order {Id}", order.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
