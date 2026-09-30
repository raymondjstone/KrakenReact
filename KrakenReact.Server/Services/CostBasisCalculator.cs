using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;

namespace KrakenReact.Server.Services;

/// <summary>Cost position for one asset after replaying its trade history.</summary>
/// <param name="PoolQuantity">Units the trade history says are still held.</param>
/// <param name="PoolCostUsd">What those units cost, in USD.</param>
/// <param name="FeesUsd">All fees paid trading the asset, in USD.</param>
public sealed record CostBasisResult(decimal PoolQuantity, decimal PoolCostUsd, decimal FeesUsd)
{
    /// <summary>Average cost of one held unit; 0 when nothing is held.</summary>
    public decimal AverageCostUsd => PoolQuantity > 0m ? PoolCostUsd / PoolQuantity : 0m;
}

/// <summary>
/// Replays trades chronologically through a moving-average cost pool. A sell removes units at the pool's average
/// cost AT THAT MOMENT, so later buys average against what is actually left. (Averaging every buy ever made, as the
/// balances endpoint used to, gives 200 for "buy 1@100, sell 1@200, buy 1@300"; the held unit really cost 300.)
/// Pure and side-effect free so it can be unit-tested and recomputed cheaply.
/// </summary>
public static class CostBasisCalculator
{
    public static Dictionary<string, CostBasisResult> Compute(
        IEnumerable<KrakenUserTrade> trades,
        Func<string, string> baseAssetOf,
        Func<string, string> quoteAssetOf,
        Func<string, decimal> usdPerUnit)
    {
        // Symbol parsing scans the symbol table, so do it once per distinct symbol rather than once per trade
        var baseCache = new Dictionary<string, string>();
        var quoteCache = new Dictionary<string, decimal>();

        var byAsset = new Dictionary<string, List<KrakenUserTrade>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trades)
        {
            var symbol = t.Symbol ?? "";
            if (symbol.Length == 0) continue;
            if (!baseCache.TryGetValue(symbol, out var asset))
                baseCache[symbol] = asset = baseAssetOf(symbol);
            if (string.IsNullOrEmpty(asset)) continue;

            if (!byAsset.TryGetValue(asset, out var list)) byAsset[asset] = list = new List<KrakenUserTrade>();
            list.Add(t);
        }

        var results = new Dictionary<string, CostBasisResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (asset, list) in byAsset)
        {
            decimal qty = 0m, cost = 0m, fees = 0m;
            foreach (var t in list.OrderBy(x => x.Timestamp))
            {
                var symbol = t.Symbol ?? "";
                if (!quoteCache.TryGetValue(symbol, out var rate))
                    quoteCache[symbol] = rate = usdPerUnit(quoteAssetOf(symbol));

                var tradeCost = (t.QuoteQuantity > 0m ? t.QuoteQuantity : t.Price * t.Quantity) * rate;
                fees += t.Fee * rate;

                if (t.Side == OrderSide.Buy)
                {
                    qty += t.Quantity;
                    cost += tradeCost;
                }
                else if (qty > 0m)
                {
                    var take = Math.Min(t.Quantity, qty);
                    cost -= cost / qty * take; // remove at the pool's average cost as it stands now
                    qty -= take;
                }
            }
            results[asset] = new CostBasisResult(qty, cost, fees);
        }
        return results;
    }
}
