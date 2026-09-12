using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>
/// Micro trading: watches 24h price change on configured pairs and, on a large enough drop,
/// places a small limit buy just under market. Once that buy fills, an automatic limit sell
/// is placed above the fill price. A per-rule rolling window caps how many buys can fire in
/// a given period to avoid over-trading a fast-dropping pair.
/// </summary>
public class MicroTradeJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly NotificationService _notify;
    private readonly ILogger<MicroTradeJob> _logger;

    public MicroTradeJob(IDbContextFactory<KrakenDbContext> dbFactory, KrakenRestService kraken, TradingStateService state, NotificationService notify, ILogger<MicroTradeJob> logger)
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

        var rules = await db.MicroTradeRules.Where(r => r.Active).ToListAsync(ct);
        foreach (var rule in rules)
        {
            try { await CheckRuleAsync(db, rule, ct); }
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

        try { await CheckRuleAsync(db, rule, ct); }
        catch (Exception ex)
        {
            rule.LastResult = $"Exception: {ex.Message}";
            _logger.LogError(ex, "[MicroTrade] Exception checking rule {Id}", rule.Id);
        }

        await MonitorFillsAsync(db, ct);
        await db.SaveChangesAsync(ct);
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

    private async Task CheckRuleAsync(KrakenDbContext db, MicroTradeRule rule, CancellationToken ct)
    {
        rule.LastCheckedAt = DateTime.UtcNow;

        var priceItem = ResolvePriceItem(rule.Symbol);
        if (priceItem == null)
        {
            rule.LastResult = "No price data available";
            return;
        }

        // 24h change: only trust the live V2 WebSocket ticker (Kraken's own change_pct) for the
        // trade trigger. PricesController falls back to a kline-derived approximation for display,
        // but that approximation compares whatever klines happen to be cached (often a daily candle
        // open, not a true rolling 24h) and can be off by several percent from the real figure —
        // it once triggered a buy at an apparent -1% when the true 24h change was +2%. A trading
        // decision needs the real number or none at all, so no fallback here.
        var changePct = priceItem.TickerData?.ChangePct24h;
        if (changePct == null)
        {
            rule.LastResult = "No live 24h change data yet — waiting for ticker";
            return;
        }

        if (changePct > -rule.DropPct)
        {
            rule.LastResult = $"No trigger — 24h change {changePct:F2}% (need <= -{rule.DropPct}%)";
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

        var buyPrice = Math.Round(currentPrice * 0.999m, 8);
        var qty = Math.Round(rule.BuyOrderTotal / buyPrice, 8);

        var sym = _state.Symbols.Values.FirstOrDefault(s =>
            s.WebsocketName.Equals(rule.Symbol, StringComparison.OrdinalIgnoreCase));

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

        // Deliberately not OR'd with the global DryRunJobs setting — this rule's own Dry run
        // checkbox is the only control shown on this screen, so it must be the sole authority.
        // Falling back to the global flag meant unchecking it here did nothing while the
        // Settings-page toggle (used for DCA etc.) was still on, which looked like a bug.
        var dryRun = rule.DryRun;
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
                $"24h {changePct:F2}% — would buy {qty} @ {buyPrice:F4} (${rule.BuyOrderTotal})");
            return;
        }

        var clientId = $"micro-{rule.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var result = await _kraken.PlaceOrderAsync(rule.Symbol, OrderSide.Buy, OrderType.Limit, qty, buyPrice, clientId);

        if (result.Success)
        {
            order.BuyOrderId = result.Data?.OrderIds?.FirstOrDefault();
            order.Status = "Buying";
            db.MicroTradeOrders.Add(order);
            rule.LastResult = $"OK — buy {qty} @ {buyPrice} (orderId={order.BuyOrderId})";
            await _notify.Pushover($"Micro Trade Buy — {rule.Symbol}",
                $"24h {changePct:F2}% drop — bought {qty} @ {buyPrice:F4} (${rule.BuyOrderTotal})");
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

    private async Task HandleBuying(KrakenDbContext db, MicroTradeOrder order, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(order.BuyOrderId)) return;
        // Give the websocket order feed time to catch up before assuming a fill
        if ((DateTime.UtcNow - order.CreatedAt).TotalSeconds < 60) return;
        if (_state.Orders.ContainsKey(order.BuyOrderId)) return; // still open

        order.BuyFilledAt = DateTime.UtcNow;

        var rule = await db.MicroTradeRules.FindAsync([order.RuleId], ct);
        var risePct = rule?.RisePct ?? 10m;
        var sellPrice = Math.Round(order.BuyPrice * (1 + risePct / 100m), 8);

        var clientId = $"micro-sell-{order.Id}-{DateTime.UtcNow:HHmmss}";
        var sellResult = await _kraken.PlaceOrderAsync(order.Symbol, OrderSide.Sell, OrderType.Limit, order.Quantity, sellPrice, clientId);

        if (sellResult.Success)
        {
            order.SellOrderId = sellResult.Data?.OrderIds?.FirstOrDefault();
            order.SellPrice = sellPrice;
            order.Status = "Selling";
            _logger.LogInformation("[MicroTrade] Buy filled for order {Id} — placed sell @ {Price}", order.Id, sellPrice);
            await _notify.Pushover($"Micro Trade Filled — {order.Symbol}",
                $"Buy filled @ {order.BuyPrice:F4}. Sell placed @ {sellPrice:F4}");
        }
        else
        {
            order.Note = $"Sell placement failed: {sellResult.Error?.Message}";
            _logger.LogError("[MicroTrade] Failed to place sell for order {Id}: {Error}", order.Id, sellResult.Error?.Message);
            await _notify.Pushover($"Micro Trade Sell Failed — {order.Symbol}", order.Note);
        }
    }

    private async Task HandleSelling(MicroTradeOrder order, MicroTradeRule? rule)
    {
        if (string.IsNullOrEmpty(order.SellOrderId)) return;

        // Stop loss — if enabled and not already triggered for this order, reprice the resting
        // profit-target sell down to near the current (lower) price so it can actually fill,
        // instead of sitting forever above a market that has since dropped further.
        if (rule is { StopLossEnabled: true } && !order.StopLossTriggered && _state.Orders.ContainsKey(order.SellOrderId))
        {
            var stopPrice = order.BuyPrice * (rule.StopLossPct / 100m);
            var priceItem = ResolvePriceItem(order.Symbol);
            var currentPrice = priceItem?.BestKline?.Close ?? 0;

            if (currentPrice > 0 && currentPrice <= stopPrice)
            {
                var cancelled = await _kraken.CancelOrderAsync(order.SellOrderId);
                if (cancelled)
                {
                    var newSellPrice = Math.Round(currentPrice * 1.001m, 8);
                    var clientId = $"micro-sl-{order.Id}-{DateTime.UtcNow:HHmmss}";
                    var result = await _kraken.PlaceOrderAsync(order.Symbol, OrderSide.Sell, OrderType.Limit, order.Quantity, newSellPrice, clientId);
                    order.StopLossTriggered = true;

                    if (result.Success)
                    {
                        var oldSellPrice = order.SellPrice;
                        order.SellOrderId = result.Data?.OrderIds?.FirstOrDefault();
                        order.SellPrice = newSellPrice;
                        order.Note = $"Stop-loss: price fell to {currentPrice:F4} (<= {stopPrice:F4}) — repriced sell {oldSellPrice:F4} -> {newSellPrice:F4}";
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

        if (_state.Orders.ContainsKey(order.SellOrderId)) return; // still open

        order.Status = "Sold";
        order.SoldAt = DateTime.UtcNow;
        _logger.LogInformation("[MicroTrade] Sell filled for order {Id}", order.Id);
        await _notify.Pushover($"Micro Trade Sold — {order.Symbol}",
            $"Sell filled @ {order.SellPrice:F4} (bought @ {order.BuyPrice:F4})");
    }
}
