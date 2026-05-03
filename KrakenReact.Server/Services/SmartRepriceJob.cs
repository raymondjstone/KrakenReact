using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class SmartRepriceJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly NotificationService _notify;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<SmartRepriceJob> _logger;

    public SmartRepriceJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        KrakenRestService kraken,
        TradingStateService state,
        NotificationService notify,
        IHubContext<TradingHub> hub,
        ILogger<SmartRepriceJob> logger)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _notify = notify;
        _hub = hub;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await db.AutoRepriceRules.Where(r => r.Active).ToListAsync(ct);
        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            try { await ProcessRule(rule); }
            catch (Exception ex)
            {
                rule.LastResult = $"Error: {ex.Message}";
                rule.LastRunAt = DateTime.UtcNow;
                _logger.LogError(ex, "[SmartReprice] Error on rule {Id} ({Symbol})", rule.Id, rule.Symbol);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsOpenStatus(string status) =>
        status.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("new", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("pending_new", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("partially_filled", StringComparison.OrdinalIgnoreCase);

    private async Task ProcessRule(AutoRepriceRule rule)
    {
        if (!rule.RepriceBuys && !rule.RepriceSells)
        {
            rule.LastResult = "Skipped — neither buys nor sells enabled";
            rule.LastRunAt = DateTime.UtcNow;
            return;
        }

        var baseAsset = _state.NormalizeOrderSymbolBase(rule.Symbol);
        var priceData = _state.LatestPrice(baseAsset);
        if (priceData == null || priceData.Close <= 0)
        {
            rule.LastResult = $"No price data (resolved base: '{baseAsset}' from '{rule.Symbol}')";
            rule.LastRunAt = DateTime.UtcNow;
            return;
        }

        var currentPrice = priceData.Close;
        var symKey = rule.Symbol.Replace("/", "");
        var minAgeCutoff = DateTime.UtcNow.AddMinutes(-rule.MinAgeMinutes);
        var maxAgeCutoff = rule.MaxAgeMinutes > 0
            ? DateTime.UtcNow.AddMinutes(-rule.MaxAgeMinutes)
            : DateTime.MinValue;

        // Gather all orders for this symbol to diagnose filter rejections
        var symbolOrders = _state.Orders.Values
            .Where(o => o.Symbol.Replace("/", "").Equals(symKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (symbolOrders.Count == 0)
        {
            var allSymbols = _state.Orders.Values.Select(o => o.Symbol).Distinct().OrderBy(s => s).ToList();
            rule.LastResult = $"No orders found for '{symKey}'. All order symbols: {string.Join(", ", allSymbols)}";
            rule.LastRunAt = DateTime.UtcNow;
            return;
        }

        var candidates = symbolOrders
            .Where(o =>
                IsOpenStatus(o.Status) &&
                o.Price > 0 &&
                o.CreateTime < minAgeCutoff &&
                (rule.MaxAgeMinutes == 0 || o.CreateTime > maxAgeCutoff) &&
                (o.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? rule.RepriceBuys : rule.RepriceSells))
            .ToList();

        if (candidates.Count == 0)
        {
            // Only detail the open/new orders; summarise closed/cancelled ones as a count
            var openOrders = symbolOrders.Where(o => IsOpenStatus(o.Status)).ToList();
            var closedCount = symbolOrders.Count - openOrders.Count;
            var reasons = openOrders.Select(o =>
            {
                var parts = new List<string>();
                if (o.Price <= 0) parts.Add("price=0");
                if (o.CreateTime >= minAgeCutoff) parts.Add($"too new ({(DateTime.UtcNow - o.CreateTime).TotalMinutes:F0}min < {rule.MinAgeMinutes}min)");
                if (rule.MaxAgeMinutes > 0 && o.CreateTime <= maxAgeCutoff) parts.Add("too old");
                var rightSide = o.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? rule.RepriceBuys : rule.RepriceSells;
                if (!rightSide) parts.Add($"side={o.Side} not enabled");
                return $"{o.Side}@{o.Price}" + (parts.Any() ? $"({string.Join(",", parts)})" : "(ok-but-in-range)");
            });
            var detail = openOrders.Any() ? string.Join("; ", reasons) : "no open orders";
            rule.LastResult = $"0 open candidates · mkt={currentPrice:F2}" +
                              (closedCount > 0 ? $" · {closedCount} closed/cancelled ignored" : "") +
                              $" · {detail}";
            rule.LastRunAt = DateTime.UtcNow;
            return;
        }

        // Resolve price decimals for this symbol so new prices are rounded correctly
        var wsName = rule.Symbol.Contains('/')
            ? rule.Symbol
            : _state.Symbols.Keys.FirstOrDefault(k => k.Replace("/", "") == symKey) ?? rule.Symbol;
        var priceDecimals = _state.Symbols.TryGetValue(wsName, out var symMeta) && symMeta.PriceDecimals > 0
            ? symMeta.PriceDecimals : 2;

        var repriced = 0;
        var placeFailed = new List<string>();
        var skipped = new List<string>();
        foreach (var order in candidates)
        {
            // For buys: reprice only when market moved UP past the order (order is below market)
            // For sells: reprice only when market moved DOWN past the order (order is above market)
            var isBuy = order.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            var drift = isBuy ? currentPrice - order.Price : order.Price - currentPrice;
            var deviation = drift / currentPrice * 100m;
            if (deviation < rule.MaxDeviationPct)
            {
                skipped.Add($"{order.Side}@{order.Price} drift={deviation:F2}%<{rule.MaxDeviationPct}%");
                continue;
            }

            var side = isBuy ? OrderSide.Buy : OrderSide.Sell;

            decimal newPrice;
            if (rule.NewPriceOffsetPct > 0)
            {
                var offsetFactor = rule.NewPriceOffsetPct / 100m;
                newPrice = side == OrderSide.Buy
                    ? Math.Round(currentPrice * (1m - offsetFactor), priceDecimals)
                    : Math.Round(currentPrice * (1m + offsetFactor), priceDecimals);
            }
            else
            {
                newPrice = side == OrderSide.Buy
                    ? Math.Round(currentPrice * 0.999m, priceDecimals)
                    : Math.Round(currentPrice * 1.001m, priceDecimals);
            }

            _logger.LogInformation("[SmartReprice] {Symbol} {Side} order {Id} deviates {Dev:F2}% — repricing {Old} → {New}",
                rule.Symbol, order.Side, order.Id, deviation, order.Price, newPrice);

            if (!await _kraken.CancelOrderAsync(order.Id))
            {
                _logger.LogWarning("[SmartReprice] Could not cancel {Id}", order.Id);
                skipped.Add($"{order.Side}@{order.Price} cancel-failed");
                continue;
            }

            // Mark old order cancelled in state immediately so balance calculations are correct
            if (_state.Orders.TryGetValue(order.Id, out var existingOrder))
                existingOrder.Status = "Cancelled";

            var safeId = order.Id.Length > 8 ? order.Id[..8] : order.Id;
            var result = await _kraken.PlaceOrderAsync(symKey, side, OrderType.Limit, order.Quantity, newPrice,
                $"repr-{safeId}-{DateTime.UtcNow:HHmm}");

            if (result.Success)
            {
                repriced++;
                // Add new order to state so balance calculations reflect it immediately
                foreach (var newId in result.Data?.OrderIds ?? [])
                {
                    var dto = new OrderDto
                    {
                        Id = newId,
                        Symbol = order.Symbol,
                        Side = order.Side,
                        Type = "Limit",
                        Status = "New",
                        Price = newPrice,
                        Quantity = order.Quantity,
                        CreateTime = DateTime.UtcNow,
                    };
                    _state.RecalculateOrderFields(dto);
                    _state.Orders[newId] = dto;
                }
            }
            else
            {
                var err = result.Error?.Message ?? "unknown error";
                _logger.LogError("[SmartReprice] Re-place failed for {Id} ({Side} {Qty}@{Price}): {Err}",
                    order.Id, order.Side, order.Quantity, newPrice, err);
                placeFailed.Add($"{order.Side} {order.Quantity}@{newPrice} — {err}");
                // Send immediate alert: order was cancelled but could not be re-placed
                await _notify.Pushover(
                    $"⚠ Reprice FAILED — {rule.Symbol}",
                    $"{order.Side} {order.Quantity} {rule.Symbol} was cancelled but re-place at {newPrice} failed: {err}");
            }
        }

        if (repriced > 0)
        {
            _state.RecalculateBalanceCoveredAmounts();
            var usdGbpRate = _state.GetUsdGbpRate();
            try { await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList(), usdGbpRate); }
            catch (Exception ex) { _logger.LogWarning(ex, "[SmartReprice] BalanceUpdate broadcast failed"); }
        }

        rule.LastResult = placeFailed.Any()
            ? $"ERROR — cancelled but re-place failed: {string.Join("; ", placeFailed)}" +
              (repriced > 0 ? $" · {repriced} succeeded" : "")
            : repriced > 0
                ? $"Repriced {repriced}/{candidates.Count} · mkt={currentPrice:F2}"
                : $"Checked {candidates.Count}, in range · mkt={currentPrice:F2}" +
                  (skipped.Any() ? $" · skipped: {string.Join("; ", skipped)}" : "");
        rule.LastRunAt = DateTime.UtcNow;

        if (repriced > 0 && !placeFailed.Any())
            await _notify.Pushover($"Smart Reprice — {rule.Symbol}", rule.LastResult);
    }
}
