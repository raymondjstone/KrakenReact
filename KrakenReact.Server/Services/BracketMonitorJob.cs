using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class BracketMonitorJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly IOrderGateway _kraken;
    private readonly TradingStateService _state;
    private readonly INotifier _notify;
    private readonly ILogger<BracketMonitorJob> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;

    public BracketMonitorJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IOrderGateway kraken,
        TradingStateService state,
        INotifier notify,
        ILogger<BracketMonitorJob> logger,
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
            _logger.LogWarning("[Bracket] Skipping tick — recent SQL timeout elsewhere, backing off");
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        List<BracketOrder> active;
        try
        {
            active = await db.BracketOrders
                .Where(b => b.Status == "Watching" || b.Status == "Active")
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout("BracketMonitorJob.LoadActive", ex);
            throw;
        }

        if (active.Count == 0) return;

        foreach (var bracket in active)
        {
            try
            {
                if (bracket.Status == "Watching")
                    await HandleWatching(bracket);
                else
                    await HandleActive(bracket);

                // Save per bracket: an order placed for one must be recorded even if a later one throws
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Bracket] Error processing bracket {Id}", bracket.Id);
            }
        }
    }

    /// <summary>True when <paramref name="price"/> has reached the stop for a position opened by <paramref name="parentSide"/>.
    /// A buy parent is protected by a sell stop below entry; a sell parent by a buy stop above it.</summary>
    internal static bool IsStopHit(string parentSide, decimal price, decimal stopPrice)
    {
        if (stopPrice <= 0 || price <= 0) return false;
        return parentSide.Equals("Buy", StringComparison.OrdinalIgnoreCase)
            ? price <= stopPrice
            : price >= stopPrice;
    }

    private decimal CurrentPrice(string symbol)
    {
        var key = _state.ResolveSymbolKey(symbol);
        if (_state.Prices.TryGetValue(key, out var item) && item.BestKline?.Close > 0)
            return item.BestKline.Close;
        return _state.LatestPrice(_state.NormalizeOrderSymbolBase(symbol))?.Close ?? 0m;
    }

    private static bool IsLive(OrderStatus status) => status is OrderStatus.Open or OrderStatus.Pending;

    /// <summary>
    /// A bracket is an entry order plus exits. On spot the two exits cannot both rest on the book — each would
    /// need the same coins — so only the take-profit rests; the stop-loss is watched here and, when hit, the
    /// take-profit is cancelled and the position closed at market (a software OCO).
    /// The entry's fill is confirmed with Kraken: "no longer in the open-orders cache" also describes a cancelled
    /// order (and, because filled orders are never evicted from the cache, a filled one never looked gone).
    /// </summary>
    private async Task HandleWatching(BracketOrder bracket)
    {
        if ((DateTime.UtcNow - bracket.CreatedAt).TotalSeconds < 30) return;

        var parent = await _kraken.GetOrderInfoAsync(bracket.KrakenOrderId);
        if (parent == null) return; // can't verify — try again next tick
        if (IsLive(parent.Status)) return;

        if (parent.QuantityFilled <= 0)
        {
            bracket.Status = "Cancelled";
            bracket.Note = $"Entry order ended {parent.Status} without filling — no exits placed";
            await _notify.Pushover($"Bracket Cancelled — {bracket.Symbol}", bracket.Note);
            return;
        }

        // Protect what was actually bought, which may be less than requested after a partial fill
        bracket.Quantity = parent.QuantityFilled;

        var oppSide = bracket.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy;
        var sym = bracket.Symbol.Replace("/", "");

        if (bracket.TakeProfitPrice > 0)
        {
            var tp = await _kraken.PlaceOrderWithRecoveryAsync(sym, oppSide, OrderType.Limit, bracket.Quantity, bracket.TakeProfitPrice,
                $"brk-tp-{bracket.Id}-{DateTime.UtcNow:HHmm}");
            if (!tp.Success)
            {
                bracket.Status = "Cancelled";
                bracket.Note = tp.Unknown
                    ? $"Could not confirm whether the take-profit was placed ({tp.Error}) — check Kraken"
                    : $"Take-profit could not be placed: {tp.Error}";
                _logger.LogError("[Bracket] {Id}: {Note}", bracket.Id, bracket.Note);
                await _notify.Pushover($"Bracket Failed — {bracket.Symbol}", bracket.Note);
                return;
            }
            bracket.TakeProfitOrderId = tp.OrderId;
        }

        bracket.Status = "Active";
        bracket.ActivatedAt = DateTime.UtcNow;
        await _notify.Pushover($"Bracket Active — {bracket.Symbol}",
            $"TP @ {bracket.TakeProfitPrice:F4} resting, stop-loss @ {bracket.StopPrice:F4} watched");
    }

    private async Task HandleActive(BracketOrder bracket)
    {
        if (bracket.ActivatedAt == null || (DateTime.UtcNow - bracket.ActivatedAt.Value).TotalSeconds < 30) return;

        decimal tpFilled = 0m;

        // Take-profit leg
        if (!string.IsNullOrEmpty(bracket.TakeProfitOrderId))
        {
            var tp = await _kraken.GetOrderInfoAsync(bracket.TakeProfitOrderId);
            if (tp == null) return; // can't verify
            tpFilled = tp.QuantityFilled;
            if (!IsLive(tp.Status))
            {
                if (tp.QuantityFilled > 0)
                {
                    // Legacy brackets also rested a stop order — release it
                    if (!string.IsNullOrEmpty(bracket.StopOrderId)) await _kraken.CancelOrderAsync(bracket.StopOrderId);
                    bracket.Status = "TookProfit";
                    await _notify.Pushover($"Bracket TP — {bracket.Symbol}", $"Take-profit @ {bracket.TakeProfitPrice:F4} filled.");
                    _logger.LogInformation("[Bracket] TP hit for bracket {Id}", bracket.Id);
                    return;
                }
                // Cancelled or expired without filling (e.g. by hand) — the stop below still applies
                bracket.TakeProfitOrderId = null;
                bracket.Note = "Take-profit order ended unfilled; stop-loss still watched";
            }
        }

        // Legacy brackets rested a real stop order: a fill means the stop hit
        if (!string.IsNullOrEmpty(bracket.StopOrderId))
        {
            var sl = await _kraken.GetOrderInfoAsync(bracket.StopOrderId);
            if (sl != null && !IsLive(sl.Status) && sl.QuantityFilled > 0)
            {
                if (!string.IsNullOrEmpty(bracket.TakeProfitOrderId)) await _kraken.CancelOrderAsync(bracket.TakeProfitOrderId);
                bracket.Status = "Stopped";
                await _notify.Pushover($"Bracket SL — {bracket.Symbol}", $"Stop-loss @ {bracket.StopPrice:F4} filled. TP cancelled.");
            }
            return; // legacy: nothing more to watch in software
        }

        // Software stop
        var price = CurrentPrice(bracket.Symbol);
        if (!IsStopHit(bracket.Side, price, bracket.StopPrice)) return;

        _logger.LogWarning("[Bracket] {Id}: price {Price} hit stop {Stop} — exiting", bracket.Id, price, bracket.StopPrice);

        // Free the coins first; if the cancel fails, do nothing this tick (the order may be filling) and retry
        if (!string.IsNullOrEmpty(bracket.TakeProfitOrderId))
        {
            if (!await _kraken.CancelOrderAsync(bracket.TakeProfitOrderId)) return;
            bracket.TakeProfitOrderId = null;
        }

        var oppSide = bracket.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy;
        var remaining = bracket.Quantity - tpFilled;
        if (remaining <= 0)
        {
            bracket.Status = "TookProfit";
            return;
        }

        var exit = await _kraken.PlaceOrderWithRecoveryAsync(bracket.Symbol.Replace("/", ""), oppSide, OrderType.Market, remaining, 0,
            $"brk-sl-{bracket.Id}-{DateTime.UtcNow:HHmm}", postOnly: false);
        if (exit.Success)
        {
            bracket.Status = "Stopped";
            bracket.Note = $"Stop-loss hit at {price:F4}; closed {remaining} at market";
            await _notify.Pushover($"Bracket SL — {bracket.Symbol}", bracket.Note);
        }
        else if (exit.Unknown)
        {
            // The market order may or may not have gone through. Retrying could sell twice, so stop tracking and ask a human.
            bracket.Status = "Stopped";
            bracket.Note = $"Stop-loss hit at {price:F4} but the exit could not be confirmed ({exit.Error}) — check Kraken";
            _logger.LogError("[Bracket] {Id}: {Note}", bracket.Id, bracket.Note);
            await _notify.Pushover($"Bracket exit UNCONFIRMED — {bracket.Symbol}", bracket.Note);
        }
        else
        {
            // Take-profit is already cancelled, so stay Active and retry the exit next tick
            bracket.Note = $"Stop-loss hit but exit failed: {exit.Error} — retrying";
            _logger.LogError("[Bracket] {Id}: {Note}", bracket.Id, bracket.Note);
            await _notify.Pushover($"Bracket exit FAILED — {bracket.Symbol}", bracket.Note);
        }
    }
}
