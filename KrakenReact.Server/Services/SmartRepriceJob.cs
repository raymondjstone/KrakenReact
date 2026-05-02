using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class SmartRepriceJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly NotificationService _notify;
    private readonly ILogger<SmartRepriceJob> _logger;

    public SmartRepriceJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        KrakenRestService kraken,
        TradingStateService state,
        NotificationService notify,
        ILogger<SmartRepriceJob> logger)
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
        var rules = await db.AutoRepriceRules.Where(r => r.Active).ToListAsync(ct);
        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            try { await ProcessRule(rule); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SmartReprice] Error on rule {Id} ({Symbol})", rule.Id, rule.Symbol);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessRule(AutoRepriceRule rule)
    {
        if (!rule.RepriceBuys && !rule.RepriceSells)
        {
            rule.LastResult = "Skipped — neither buys nor sells enabled";
            rule.LastRunAt = DateTime.UtcNow;
            return;
        }

        var baseAsset = TradingStateService.NormalizeAsset(rule.Symbol.Split('/')[0]);
        var priceData = _state.LatestPrice(baseAsset);
        if (priceData == null || priceData.Close <= 0)
        {
            rule.LastResult = "No price data";
            rule.LastRunAt = DateTime.UtcNow;
            return;
        }

        var currentPrice = priceData.Close;
        var symKey = rule.Symbol.Replace("/", "");
        var minAgeCutoff = DateTime.UtcNow.AddMinutes(-rule.MinAgeMinutes);
        var maxAgeCutoff = rule.MaxAgeMinutes > 0
            ? DateTime.UtcNow.AddMinutes(-rule.MaxAgeMinutes)
            : DateTime.MinValue;

        var candidates = _state.Orders.Values
            .Where(o =>
                (o.Symbol.Equals(symKey, StringComparison.OrdinalIgnoreCase) ||
                 o.Symbol.Equals(rule.Symbol, StringComparison.OrdinalIgnoreCase)) &&
                o.Status == "Open" &&
                o.Price > 0 &&
                o.CreateTime < minAgeCutoff &&
                (rule.MaxAgeMinutes == 0 || o.CreateTime > maxAgeCutoff) &&
                (o.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? rule.RepriceBuys : rule.RepriceSells))
            .ToList();

        var repriced = 0;
        foreach (var order in candidates)
        {
            var deviation = Math.Abs(order.Price - currentPrice) / currentPrice * 100m;
            if (deviation < rule.MaxDeviationPct) continue;

            var side = order.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;

            // Compute new price: 0 = quasi-market (0.1% inside); > 0 = passive limit at specified offset
            decimal newPrice;
            if (rule.NewPriceOffsetPct > 0)
            {
                var offsetFactor = rule.NewPriceOffsetPct / 100m;
                newPrice = side == OrderSide.Buy
                    ? Math.Round(currentPrice * (1m - offsetFactor), 2)
                    : Math.Round(currentPrice * (1m + offsetFactor), 2);
            }
            else
            {
                newPrice = side == OrderSide.Buy
                    ? Math.Round(currentPrice * 0.999m, 2)
                    : Math.Round(currentPrice * 1.001m, 2);
            }

            _logger.LogInformation("[SmartReprice] {Symbol} {Side} order {Id} deviates {Dev:F2}% — repricing to {New}",
                rule.Symbol, order.Side, order.Id, deviation, newPrice);

            if (!await _kraken.CancelOrderAsync(order.Id))
            {
                _logger.LogWarning("[SmartReprice] Could not cancel {Id}", order.Id);
                continue;
            }

            var safeId = order.Id.Length > 8 ? order.Id[..8] : order.Id;
            var result = await _kraken.PlaceOrderAsync(symKey, side, OrderType.Limit, order.Quantity, newPrice,
                $"repr-{safeId}-{DateTime.UtcNow:HHmm}");

            if (result.Success) repriced++;
            else _logger.LogError("[SmartReprice] Re-place failed for {Id}: {Err}", order.Id, result.Error?.Message);
        }

        rule.LastResult = repriced > 0
            ? $"Repriced {repriced} order(s)"
            : $"Checked {candidates.Count}, none needed repricing";
        rule.LastRunAt = DateTime.UtcNow;

        if (repriced > 0)
            await _notify.Pushover($"Smart Reprice — {rule.Symbol}", rule.LastResult);
    }
}
