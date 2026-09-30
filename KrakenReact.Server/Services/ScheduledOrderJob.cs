using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class ScheduledOrderJob
{
    /// <summary>How long a row may sit in "Placing" before it is assumed to have been orphaned by a crash.</summary>
    private static readonly TimeSpan StalePlacing = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly IOrderGateway _kraken;
    private readonly TradingStateService _state;
    private readonly INotifier _notify;
    private readonly ILogger<ScheduledOrderJob> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;

    public ScheduledOrderJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IOrderGateway kraken,
        TradingStateService state,
        INotifier notify,
        ILogger<ScheduledOrderJob> logger,
        SqlTimeoutDiagnostics sqlDiag)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _notify = notify;
        _logger = logger;
        _sqlDiag = sqlDiag;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (_sqlDiag.RecentTimeout(SqlTimeoutDiagnostics.RecentTimeoutBackoff))
        {
            _logger.LogWarning("[ScheduledOrders] Skipping tick — recent SQL timeout elsewhere, backing off");
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Short timeout for the polling read — every-minute job; if it can't get in fast,
        // fail fast and free the pool slot rather than holding it for 120s.
        if (db.Database.IsRelational()) // the in-memory test provider has no command timeout
            db.Database.SetCommandTimeout(TimeSpan.FromSeconds(15));

        List<ScheduledOrder> pending;
        try
        {
            pending = await db.ScheduledOrders
                .Where(o => (o.Status == "Pending" && o.ScheduledAt <= DateTime.UtcNow) || o.Status == "Placing")
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout("ScheduledOrderJob.PollPending", ex);
            throw;
        }

        if (pending.Count == 0) return;

        // A row still "Placing" from an earlier run means the process stopped between sending the order and
        // recording the result. It may well exist on Kraken, so it is never re-sent: flag it for a human.
        foreach (var stale in pending.Where(o => o.Status == "Placing" && DateTime.UtcNow - (o.ExecutedAt ?? o.ScheduledAt) > StalePlacing).ToList())
        {
            stale.Status = "Unconfirmed";
            stale.ExecutedAt = null;
            stale.ErrorMessage = "Placement was interrupted before the result was recorded — check Kraken for this order before re-creating it";
            _logger.LogError("[ScheduledOrders] Order {Id} was interrupted mid-placement — marked Unconfirmed", stale.Id);
            await _notify.Pushover($"Scheduled Order Unconfirmed — {stale.Symbol}", stale.ErrorMessage);
        }
        pending = pending.Where(o => o.Status == "Pending").ToList();
        await db.SaveChangesAsync(ct);
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
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                var side = order.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase)
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // Write-ahead: persist "Placing" BEFORE talking to Kraken. If the save after the order fails (or the
                // process dies) the row is no longer "Pending", so the next tick cannot place the same order again.
                order.Status = "Placing";
                order.ExecutedAt = DateTime.UtcNow; // placing timestamp for the stale check; cleared unless it really executes
                await db.SaveChangesAsync(ct);

                var clientId = KrakenReact.Server.Utils.ClientOrderId.GenerateTimestampWithPrefix($"sched-{order.Id}-");
                var result = await _kraken.PlaceOrderWithRecoveryAsync(
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
                        $"{order.Side} {order.Quantity} {order.Symbol} @ {order.Price:F4} (order id: {result.OrderId})");
                }
                else if (result.Unknown)
                {
                    order.Status = "Unconfirmed";
                    order.ExecutedAt = null;
                    order.ErrorMessage = $"Could not confirm whether Kraken accepted the order ({result.Error}) — check Kraken before re-creating it";
                    _logger.LogError("[ScheduledOrders] Order {Id} placement unconfirmed: {Error}", order.Id, result.Error);
                    await _notify.Pushover($"Scheduled Order Unconfirmed — {order.Symbol}", order.ErrorMessage);
                }
                else
                {
                    order.Status = "Failed";
                    order.ExecutedAt = null;
                    order.ErrorMessage = result.Error ?? "Unknown error";
                    _logger.LogError("[ScheduledOrders] Order {Id} failed: {Error}", order.Id, order.ErrorMessage);
                    await _notify.Pushover(
                        $"Scheduled Order Failed — {order.Symbol}",
                        $"Failed to place {order.Side} {order.Quantity} {order.Symbol}: {order.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                // The order may have reached Kraken before the exception, so this is not a definite failure
                order.Status = order.Status == "Placing" ? "Unconfirmed" : "Failed";
                order.ExecutedAt = null;
                order.ErrorMessage = ex.Message;
                _logger.LogError(ex, "[ScheduledOrders] Exception processing order {Id}", order.Id);
            }

            // Record each order as soon as it is done rather than once at the end of the batch
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) { _logger.LogCritical(ex, "[ScheduledOrders] Could not record result for order {Id} ({Status})", order.Id, order.Status); }
        }
    }
}
