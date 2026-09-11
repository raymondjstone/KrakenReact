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

    private async Task CheckRuleAsync(KrakenDbContext db, MicroTradeRule rule, CancellationToken ct)
    {
        rule.LastCheckedAt = DateTime.UtcNow;

        var key = _state.ResolveSymbolKey(rule.Symbol);
        if (!_state.Prices.TryGetValue(key, out var priceItem))
        {
            rule.LastResult = "No price data available";
            return;
        }

        // 24h change: prefer V2 WebSocket real-time data (change_pct from Kraken), fall back to kline
        // history — mirrors PricesController.GetAll, whose fallback is why the dashboard shows a
        // change % here even when the live ticker hasn't pushed an update yet.
        decimal changePct;
        if (priceItem.TickerData?.ChangePct24h.HasValue == true)
            changePct = priceItem.TickerData.ChangePct24h.Value;
        else
            changePct = priceItem.CloseMovementDiff(1);

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

        var dryRun = rule.DryRun || _state.DryRunJobs;
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

        foreach (var order in pending)
        {
            try
            {
                if (order.Status == "Buying")
                    await HandleBuying(db, order, ct);
                else
                    await HandleSelling(order);
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

    private async Task HandleSelling(MicroTradeOrder order)
    {
        if (string.IsNullOrEmpty(order.SellOrderId)) return;
        if (_state.Orders.ContainsKey(order.SellOrderId)) return; // still open

        order.Status = "Sold";
        order.SoldAt = DateTime.UtcNow;
        _logger.LogInformation("[MicroTrade] Sell filled for order {Id}", order.Id);
        await _notify.Pushover($"Micro Trade Sold — {order.Symbol}",
            $"Sell filled @ {order.SellPrice:F4} (bought @ {order.BuyPrice:F4})");
    }
}
