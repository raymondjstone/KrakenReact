using KrakenReact.Server.DTOs;

namespace KrakenReact.Server.Services;

public partial class TradingStateService
{
    private static readonly HashSet<string> FiatForCostBasis = new(StringComparer.OrdinalIgnoreCase)
        { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };

    private IReadOnlyDictionary<string, CostBasisResult> _costBasis =
        new Dictionary<string, CostBasisResult>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cost position per (normalized) base asset, rebuilt whenever the cached trades change.</summary>
    public IReadOnlyDictionary<string, CostBasisResult> CostBasis => _costBasis;

    /// <summary>Rebuilds <see cref="CostBasis"/> from the cached trades. Called when trades refresh, so the stop-loss /
    /// take-profit / profit-ladder jobs no longer depend on someone loading the balances page to populate it.</summary>
    public void RecalculateCostBasis()
    {
        var gbpFactor = GetUsdGbpRate() is var r && r > 0 ? 1m / r : 1.27m;
        var eurFactor = Prices.TryGetValue("EUR/USD", out var eur) && eur.BestKline?.Close > 0 ? eur.BestKline.Close : 1.08m;

        decimal UsdPerUnit(string quote) => quote switch
        {
            "GBP" => gbpFactor,
            "EUR" => eurFactor,
            _ => 1m, // USD and the USD stablecoins; anything unknown is assumed USD as before
        };

        _costBasis = CostBasisCalculator.Compute(CachedTrades, NormalizeOrderSymbolBase, NormalizeOrderSymbolQuote, UsdPerUnit);
    }

    /// <summary>
    /// Fills a balance's cost basis, fees and profit/loss from <see cref="CostBasis"/>. Cheap (a dictionary lookup), so
    /// it is safe to call wherever a balance is built or its price changes.
    /// Units held beyond what the trade history accounts for (staking rewards, deposits) carry no cost, as before.
    /// </summary>
    public void ApplyCostBasis(BalanceDto balance)
    {
        if (FiatForCostBasis.Contains(balance.Asset)) return;

        if (!_costBasis.TryGetValue(NormalizeAsset(balance.Asset), out var position) || position.PoolQuantity <= 0m || balance.Total <= 0m)
        {
            balance.TotalCostBasis = null;
            balance.NetProfitLoss = null;
            balance.NetProfitLossPercentage = null;
            return;
        }

        var basis = position.AverageCostUsd * Math.Min(balance.Total, position.PoolQuantity);
        balance.TotalCostBasis = basis;
        balance.TotalFees = position.FeesUsd;
        if (basis > 0m)
        {
            balance.NetProfitLoss = balance.LatestValue - basis - position.FeesUsd;
            balance.NetProfitLossPercentage = balance.NetProfitLoss / basis * 100m;
        }
    }
}
