using CryptoExchange.Net.Objects;
using Hangfire;
using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>
/// Micro trading: watches the price change over each rule's configured interval (1h/4h/6h/12h/24h)
/// on configured pairs and, on a large enough drop, places a small limit buy just under market.
/// Once that buy fills, an automatic limit sell is placed above the fill price. A per-rule rolling
/// window caps how many buys can fire in a given period to avoid over-trading a fast-dropping pair.
/// </summary>
public class MicroTradeJob
{
    /// <summary>AppSettings key for the Micro Trading emergency stop toggle — see MicroTradeController.</summary>
    public const string EmergencyStopKey = "MicroTradeEmergencyStop";

    /// <summary>Least-significant-digit shrink retries a sell goes through before giving up if Kraken
    /// rejects it (typically a rounding mismatch between what we think we hold and Kraken's ledger).</summary>
    private const int MaxSellAttempts = 5;

    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly PriceChangeService _priceChange;
    private readonly NotificationService _notify;
    private readonly ILogger<MicroTradeJob> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;

    public MicroTradeJob(IDbContextFactory<KrakenDbContext> dbFactory, KrakenRestService kraken, TradingStateService state, PriceChangeService priceChange, NotificationService notify, ILogger<MicroTradeJob> logger, SqlTimeoutDiagnostics sqlDiag)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _priceChange = priceChange;
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
            _logger.LogWarning("[MicroTrade] Skipping tick — recent SQL timeout elsewhere, backing off");
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        List<MicroTradeRule> rules;
        try
        {
            rules = await db.MicroTradeRules.Where(r => r.Active).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout("MicroTradeJob.LoadRules", ex);
            throw;
        }

        var emergencyStop = await IsEmergencyStopActiveAsync(db, ct);

        foreach (var rule in rules)
        {
            try { await CheckRuleAsync(db, rule, emergencyStop, ct); }
            catch (Exception ex)
            {
                rule.LastResult = $"Exception: {ex.Message}";
                _logger.LogError(ex, "[MicroTrade] Exception checking rule {Id}", rule.Id);
            }
        }

        await MonitorFillsAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteRuleAsync(int ruleId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.MicroTradeRules.FindAsync([ruleId], ct);
        if (rule == null) return;

        var emergencyStop = await IsEmergencyStopActiveAsync(db, ct);

        try { await CheckRuleAsync(db, rule, emergencyStop, ct); }
        catch (Exception ex)
        {
            rule.LastResult = $"Exception: {ex.Message}";
            _logger.LogError(ex, "[MicroTrade] Exception checking rule {Id}", rule.Id);
        }

        await MonitorFillsAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> IsEmergencyStopActiveAsync(KrakenDbContext db, CancellationToken ct)
    {
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == EmergencyStopKey, ct);
        return setting != null && string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a rule's symbol to the PriceDataItem holding live ticker data for it.
    /// <para>
    /// Different collectors key the same pair under different spellings — e.g. the V2 ticker feed
    /// may create "BTC/USD" while the REST kline loader later creates a separate "XBT/USD" entry
    /// for the same market. TradingStateService.ResolveSymbolKey short-circuits on an exact match,
    /// so once a second entry spelled exactly like the rule's own Symbol exists, it wins even though
    /// it has no TickerData — which looks like the 24h change "disappearing" after having worked.
    /// If the directly-resolved entry has no ticker data, fall back to scanning for a sibling entry
    /// with the same normalized base/quote that does.
    /// </para>
    /// </summary>
    private PriceDataItem? ResolvePriceItem(string symbol)
    {
        var key = _state.ResolveSymbolKey(symbol);
        _state.Prices.TryGetValue(key, out var priceItem);
        if (priceItem?.TickerData?.ChangePct24h != null) return priceItem;

        var parts = symbol.Split('/');
        if (parts.Length == 2)
        {
            var normBase = TradingStateService.NormalizeAsset(parts[0]);
            var normCcy = TradingStateService.NormalizeAsset(parts[1]);
            var sibling = _state.Prices.Values.FirstOrDefault(p =>
                p.TickerData?.ChangePct24h != null &&
                TradingStateService.NormalizeAsset(p.Base) == normBase &&
                TradingStateService.NormalizeAsset(p.CCY) == normCcy);
            if (sibling != null) return sibling;
        }

        return priceItem;
    }

    private async Task CheckRuleAsync(KrakenDbContext db, MicroTradeRule rule, bool emergencyStopActive, CancellationToken ct)
    {
        rule.LastCheckedAt = DateTime.UtcNow;

        var priceItem = ResolvePriceItem(rule.Symbol);
        if (priceItem == null)
        {
            rule.LastResult = "No price data available";
            return;
        }

        var intervalHours = rule.DropIntervalHours <= 0 ? 24 : rule.DropIntervalHours;
        var changePct = await _priceChange.GetChangeAsync(rule.Symbol, intervalHours, priceItem);
        if (changePct == null)
        {
            rule.LastResult = intervalHours == 24
                ? "No live 24h change data yet — waiting for ticker"
                : $"No {intervalHours}h change data yet — insufficient kline history";
            return;
        }

        if (changePct > -rule.DropPct)
        {
            rule.LastResult = $"No trigger — {intervalHours}h change {changePct:F2}% (need <= -{rule.DropPct}%)";
            return;
        }

        // Cooldown — no order on this pair (from any rule) within the last CooldownHours,
        // regardless of the window/max-orders limit below.
        if (rule.CooldownHours > 0)
        {
            var cooldownCutoff = DateTime.UtcNow.AddHours(-rule.CooldownHours);
            var lastOrder = await db.MicroTradeOrders
                .Where(o => o.Symbol == rule.Symbol && o.Status != "Cancelled")
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (lastOrder != null && lastOrder.CreatedAt > cooldownCutoff)
            {
                var hoursAgo = (DateTime.UtcNow - lastOrder.CreatedAt).TotalHours;
                rule.LastResult = $"Skip — cooldown active, last order {hoursAgo:F1}h ago (need >= {rule.CooldownHours}h)";
                return;
            }
        }

        var windowStart = DateTime.UtcNow.AddHours(-rule.WindowHours);
        var recentCount = await db.MicroTradeOrders.CountAsync(
            o => o.RuleId == rule.Id && o.CreatedAt >= windowStart && o.Status != "Cancelled", ct);
        if (recentCount >= rule.MaxOrdersPerWindow)
        {
            rule.LastResult = $"Skip — rate limit {recentCount}/{rule.MaxOrdersPerWindow} orders in last {rule.WindowHours}h";
            return;
        }

        var currentPrice = priceItem.BestKline?.Close ?? 0;
        if (currentPrice <= 0)
        {
            rule.LastResult = "No current price available";
            return;
        }

        var sym = _state.Symbols.Values.FirstOrDefault(s =>
            s.WebsocketName.Equals(rule.Symbol, StringComparison.OrdinalIgnoreCase));
        var priceDecimals = sym?.PriceDecimals > 0 ? sym.PriceDecimals : 8;
        var lotDecimals = sym?.LotDecimals > 0 ? sym.LotDecimals : 8;

        var buyPrice = Math.Round(currentPrice * 0.999m, priceDecimals);
        // Floor (never round up) to the pair's real lot precision — Kraken rejects a quantity with
        // more decimal places than the pair allows outright, which was the root cause behind most
        // rounding failures, and flooring also guarantees we never spend more than BuyOrderTotal.
        var qty = FloorToDecimals(rule.BuyOrderTotal / buyPrice, lotDecimals);

        var minQty = sym?.OrderMin ?? 0.0001m;
        if (qty < minQty)
        {
            rule.LastResult = $"Qty {qty} below min {minQty}";
            return;
        }

        var minValue = sym?.MinValue ?? 0m;
        if (minValue > 0 && qty * buyPrice < minValue)
        {
            rule.LastResult = $"Order value {qty * buyPrice:F2} below min value {minValue}";
            return;
        }

        // Emergency stop — blocks all new buy creation (including dry-run simulated ones) until
        // switched off from the screen. Existing open positions are still monitored/sold as normal;
        // this only stops new buys from being created.
        if (emergencyStopActive)
        {
            rule.LastResult = $"Skip — EMERGENCY STOP active (would buy {qty} @ {buyPrice})";
            return;
        }

        // Deliberately not OR'd with the global DryRunJobs setting — this rule's own Dry run
        // checkbox is the only control shown on this screen, so it must be the sole authority.
        // Falling back to the global flag meant unchecking it here did nothing while the
        // Settings-page toggle (used for DCA etc.) was still on, which looked like a bug.
        var dryRun = rule.DryRun;

        // Confirm the quote currency actually has enough available balance right now before
        // placing a real order — a rule can trigger off a price move faster than the balance
        // cache updates, or funds can already be tied up in other open orders.
        if (!dryRun)
        {
            var quoteCcy = _state.NormalizeOrderSymbolQuote(rule.Symbol);
            var orderCost = qty * buyPrice;
            _state.Balances.TryGetValue(quoteCcy, out var quoteBalance);
            var available = quoteBalance?.Available ?? 0m;
            if (quoteBalance == null || available < orderCost)
            {
                rule.LastResult = $"Skip — insufficient {quoteCcy} balance ({available:F2} available, need {orderCost:F2})";
                _logger.LogWarning("[MicroTrade] Skip buy for rule {Id} — insufficient {Ccy} balance ({Available} < {Cost})",
                    rule.Id, quoteCcy, available, orderCost);
                return;
            }
        }

        var order = new MicroTradeOrder
        {
            RuleId = rule.Id,
            Symbol = rule.Symbol,
            BuyPrice = buyPrice,
            Quantity = qty,
            CreatedAt = DateTime.UtcNow,
            DryRun = dryRun,
        };

        if (dryRun)
        {
            order.Status = "DryRun";
            order.Note = $"DRY RUN — would buy {qty} @ {buyPrice}";
            db.MicroTradeOrders.Add(order);
            rule.LastResult = order.Note;
            _logger.LogInformation("[MicroTrade] DRY RUN — would place buy: {Symbol} {Qty} @ {Price}", rule.Symbol, qty, buyPrice);
            await _notify.Pushover($"DRY RUN — Micro Trade {rule.Symbol}",
                $"{intervalHours}h {changePct:F2}% — would buy {qty} @ {buyPrice:F4} (${rule.BuyOrderTotal})");
            return;
        }

        // For micro trading, do not set clientOrderId so Kraken assigns one — avoids cl_ord_id validation errors
        var result = await _kraken.PlaceOrderAsync(rule.Symbol, OrderSide.Buy, OrderType.Limit, qty, buyPrice, null);

        if (result.Success)
        {
            order.BuyOrderId = result.Data?.OrderIds?.FirstOrDefault();
            order.Status = "Buying";
            db.MicroTradeOrders.Add(order);
            rule.LastResult = $"OK — buy {qty} @ {buyPrice} (orderId={order.BuyOrderId})";
            await _notify.Pushover($"Micro Trade Buy — {rule.Symbol}",
                $"{intervalHours}h {changePct:F2}% drop — bought {qty} @ {buyPrice:F4} (${rule.BuyOrderTotal})");
        }
        else
        {
            rule.LastResult = $"Error: {result.Error?.Message}";
            _logger.LogError("[MicroTrade] Buy failed for rule {Id}: {Error}", rule.Id, result.Error?.Message);
        }
    }

    private async Task MonitorFillsAsync(KrakenDbContext db, CancellationToken ct)
    {
        var pending = await db.MicroTradeOrders
            .Where(o => o.Status == "Buying" || o.Status == "Selling")
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var ruleIds = pending.Select(o => o.RuleId).Distinct().ToList();
        var rules = await db.MicroTradeRules.Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        foreach (var order in pending)
        {
            try
            {
                if (order.Status == "Buying")
                    await HandleBuying(db, order, ct);
                else
                {
                    rules.TryGetValue(order.RuleId, out var rule);
                    await HandleSelling(order, rule);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MicroTrade] Error monitoring order {Id}", order.Id);
            }
        }
    }

    /// <summary>
    /// True if the order is still resting on the book. The websocket execution feed updates an
    /// order's Status in place as it fills/closes but never removes it from _state.Orders (that
    /// only happens ~30 days later via the REST reconciliation in BackgroundTaskService), so a
    /// plain ContainsKey check would treat a filled/closed order as still open forever.
    /// </summary>
    private bool IsOrderStillOpen(string orderId) =>
        _state.Orders.TryGetValue(orderId, out var o) && TradingStateService.IsOpenOrderStatus(o.Status);

    private async Task HandleBuying(KrakenDbContext db, MicroTradeOrder order, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(order.BuyOrderId)) return;
        // Give the websocket order feed time to catch up before assuming a fill
        if ((DateTime.UtcNow - order.CreatedAt).TotalSeconds < 60) return;
        if (IsOrderStillOpen(order.BuyOrderId)) return; // still open

        order.BuyFilledAt = DateTime.UtcNow;

        var rule = await db.MicroTradeRules.FindAsync([order.RuleId], ct);
        var risePct = rule?.RisePct ?? 10m;
        var sellPrice = Math.Round(order.BuyPrice * (1 + risePct / 100m), GetPriceDecimals(order.Symbol));

        // Sell exactly what the buy actually filled, not what we expected to get — Kraken's own
        // fill quantity (vol_exec) is authoritative; the websocket order feed never populates this,
        // so we look it up directly. Fall back to the originally requested quantity only if that
        // lookup fails outright.
        var filledOrder = await _kraken.GetOrderInfoAsync(order.BuyOrderId!);
        var actualQty = filledOrder is { QuantityFilled: > 0 } ? filledOrder.QuantityFilled : order.Quantity;
        order.Quantity = actualQty;

        var (sellResult, soldQty) = await PlaceSellWithRetryAsync(order.Symbol, actualQty, sellPrice);

        if (sellResult.Success)
        {
            order.Quantity = soldQty;
            order.SellOrderId = sellResult.Data?.OrderIds?.FirstOrDefault();
            order.SellPrice = sellPrice;
            order.Status = "Selling";
            if (soldQty != actualQty)
                order.Note = $"Sell quantity reduced {actualQty} -> {soldQty} after Kraken rejected the exact fill quantity";
            _logger.LogInformation("[MicroTrade] Buy filled for order {Id} — placed sell of {Qty} @ {Price}", order.Id, soldQty, sellPrice);
            await _notify.Pushover($"Micro Trade Filled — {order.Symbol}",
                $"Buy filled @ {order.BuyPrice:F4}. Sell of {soldQty} placed @ {sellPrice:F4}");
        }
        else
        {
            order.Note = $"Sell placement failed after {MaxSellAttempts} attempts (last tried qty {soldQty}): {sellResult.Error?.Message}";
            _logger.LogError("[MicroTrade] Failed to place sell for order {Id}: {Error}", order.Id, sellResult.Error?.Message);
            await _notify.Pushover($"Micro Trade Sell Failed — {order.Symbol}", order.Note);
        }
    }

    /// <summary>
    /// Places a sell for <paramref name="quantity"/>, first floored to the pair's actual lot
    /// precision — Kraken's own fill quantities and our own budget-derived quantities can both carry
    /// more decimal places than the pair allows, and that's rejected outright rather than rounded on
    /// Kraken's side. If it's still rejected — typically a small balance/ledger mismatch — the
    /// quantity is shrunk slightly and the placement retried.
    /// </summary>
    private async Task<(WebCallResult<KrakenPlacedOrder> Result, decimal Quantity)> PlaceSellWithRetryAsync(string symbol, decimal quantity, decimal price)
    {
        var lotDecimals = GetLotDecimals(symbol);
        var qty = FloorToDecimals(quantity, lotDecimals);
        if (qty <= 0) qty = quantity; // flooring collapsed it to zero (quantity smaller than the pair's lot step) — fall back and let Kraken's own error surface

        WebCallResult<KrakenPlacedOrder> result;
        for (var attempt = 1; ; attempt++)
        {
            // Let Kraken assign clientOrderId for auto-sells to avoid id format issues
            result = await _kraken.PlaceOrderAsync(symbol, OrderSide.Sell, OrderType.Limit, qty, price, null);
            if (result.Success || attempt >= MaxSellAttempts) return (result, qty);

            var shrunk = ShrinkQuantitySlightly(qty, lotDecimals);
            _logger.LogWarning("[MicroTrade] Sell of {Qty} {Symbol} failed ({Error}) — retrying with {Shrunk}",
                qty, symbol, result.Error?.Message, shrunk);
            qty = shrunk;
        }
    }

    /// <summary>Trims a rejected sell quantity so the next retry has a real chance of succeeding.
    /// If the value still carries more decimal places than the pair's lot precision allows, it's
    /// floored straight down to that precision (the usual cause of a rejection — trimming just the
    /// least-significant digit of an over-precise value can land back on a numerically identical
    /// value when trailing digits are zero, which never actually reduces precision and just burns
    /// retries). Once it's already at the pair's precision, one unit at that precision is subtracted
    /// instead, which is the right move for a small balance/ledger mismatch.</summary>
    private static decimal ShrinkQuantitySlightly(decimal qty, int lotDecimals)
    {
        var str = qty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dotIdx = str.IndexOf('.');
        var currentDecimals = dotIdx < 0 ? 0 : str.Length - dotIdx - 1;

        if (currentDecimals > lotDecimals)
            return FloorToDecimals(qty, lotDecimals);

        var step = 1m / (decimal)Math.Pow(10, Math.Max(lotDecimals, 0));
        var shrunk = qty - step;
        return shrunk > 0 ? shrunk : 0;
    }

    private int GetLotDecimals(string symbol)
    {
        var sym = _state.Symbols.Values.FirstOrDefault(s => s.WebsocketName.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        return sym?.LotDecimals > 0 ? sym.LotDecimals : 8;
    }

    private int GetPriceDecimals(string symbol)
    {
        var sym = _state.Symbols.Values.FirstOrDefault(s => s.WebsocketName.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        return sym?.PriceDecimals > 0 ? sym.PriceDecimals : 8;
    }

    private static decimal FloorToDecimals(decimal value, int decimals)
    {
        if (decimals < 0) return value;
        var factor = (decimal)Math.Pow(10, decimals);
        return Math.Floor(value * factor) / factor;
    }

    private async Task HandleSelling(MicroTradeOrder order, MicroTradeRule? rule)
    {
        if (string.IsNullOrEmpty(order.SellOrderId)) return;

        // Stop loss — if enabled and not already triggered for this order, reprice the resting
        // profit-target sell down to near the current (lower) price so it can actually fill,
        // instead of sitting forever above a market that has since dropped further.
        if (rule is { StopLossEnabled: true } && !order.StopLossTriggered && IsOrderStillOpen(order.SellOrderId))
        {
            var stopPrice = order.BuyPrice * (rule.StopLossPct / 100m);
            var priceItem = ResolvePriceItem(order.Symbol);
            var currentPrice = priceItem?.BestKline?.Close ?? 0;

            if (currentPrice > 0 && currentPrice <= stopPrice)
            {
                var cancelled = await _kraken.CancelOrderAsync(order.SellOrderId);
                if (cancelled)
                {
                    var newSellPrice = Math.Round(currentPrice * 1.001m, GetPriceDecimals(order.Symbol));
                    var (result, soldQty) = await PlaceSellWithRetryAsync(order.Symbol, order.Quantity, newSellPrice);
                    order.StopLossTriggered = true;

                    if (result.Success)
                    {
                        var oldSellPrice = order.SellPrice;
                        var oldQty = order.Quantity;
                        order.Quantity = soldQty;
                        order.SellOrderId = result.Data?.OrderIds?.FirstOrDefault();
                        order.SellPrice = newSellPrice;
                        order.Note = $"Stop-loss: price fell to {currentPrice:F4} (<= {stopPrice:F4}) — repriced sell {oldSellPrice:F4} -> {newSellPrice:F4}"
                            + (soldQty != oldQty ? $" (qty reduced {oldQty} -> {soldQty} after rejection)" : "");
                        _logger.LogInformation("[MicroTrade] Stop-loss repriced sell for order {Id}: {Note}", order.Id, order.Note);
                        await _notify.Pushover($"Micro Trade Stop-Loss — {order.Symbol}", order.Note);
                    }
                    else
                    {
                        order.Note = $"Stop-loss: sell cancelled but reprice failed: {result.Error?.Message}";
                        _logger.LogError("[MicroTrade] Stop-loss reprice failed for order {Id}: {Error}", order.Id, result.Error?.Message);
                        await _notify.Pushover($"Micro Trade Stop-Loss Failed — {order.Symbol}", order.Note);
                    }
                }
                return; // don't also check for a fill this same pass
            }
        }

        if (IsOrderStillOpen(order.SellOrderId)) return; // still open

        order.Status = "Sold";
        order.SoldAt = DateTime.UtcNow;
        _logger.LogInformation("[MicroTrade] Sell filled for order {Id}", order.Id);
        await _notify.Pushover($"Micro Trade Sold — {order.Symbol}",
            $"Sell filled @ {order.SellPrice:F4} (bought @ {order.BuyPrice:F4})");
    }
}
