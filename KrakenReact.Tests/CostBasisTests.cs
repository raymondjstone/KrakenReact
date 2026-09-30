using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class CostBasisTests
{
    private static KrakenUserTrade Trade(int day, OrderSide side, decimal qty, decimal price, decimal fee = 0m, string symbol = "XBT/USD") => new()
    {
        Symbol = symbol,
        Side = side,
        Quantity = qty,
        Price = price,
        QuoteQuantity = qty * price,
        Fee = fee,
        Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day),
    };

    private static Dictionary<string, CostBasisResult> Run(params KrakenUserTrade[] trades) =>
        CostBasisCalculator.Compute(trades, s => s.Split('/')[0], s => s.Contains('/') ? s.Split('/')[1] : "USD", q => q == "GBP" ? 1.25m : 1m);

    [Fact]
    public void MovingAverage_BuySellBuy_HeldUnitCostsWhatWasPaidLast()
    {
        // The old "average of every buy ever" method reported 200 here; the unit still held cost 300
        var r = Run(
            Trade(0, OrderSide.Buy, 1m, 100m),
            Trade(1, OrderSide.Sell, 1m, 200m),
            Trade(2, OrderSide.Buy, 1m, 300m))["XBT"];

        Assert.Equal(1m, r.PoolQuantity);
        Assert.Equal(300m, r.AverageCostUsd);
    }

    [Fact]
    public void PartialSell_KeepsTheAverageCostOfWhatRemains()
    {
        var r = Run(
            Trade(0, OrderSide.Buy, 2m, 100m),
            Trade(1, OrderSide.Buy, 2m, 200m),   // pool: 4 @ avg 150
            Trade(2, OrderSide.Sell, 1m, 500m))["XBT"]; // sells 1 at avg cost 150

        Assert.Equal(3m, r.PoolQuantity);
        Assert.Equal(150m, r.AverageCostUsd);
        Assert.Equal(450m, r.PoolCostUsd);
    }

    [Fact]
    public void TradesAreReplayedInTimeOrder_NotListOrder()
    {
        var r = Run(
            Trade(2, OrderSide.Buy, 1m, 300m),
            Trade(0, OrderSide.Buy, 1m, 100m),
            Trade(1, OrderSide.Sell, 1m, 200m))["XBT"];

        Assert.Equal(1m, r.PoolQuantity);
        Assert.Equal(300m, r.AverageCostUsd); // 100 bought, sold, then 300 bought
    }

    [Fact]
    public void SellingMoreThanThePoolHolds_DoesNotGoNegative()
    {
        var r = Run(Trade(0, OrderSide.Buy, 1m, 100m), Trade(1, OrderSide.Sell, 5m, 100m))["XBT"];
        Assert.Equal(0m, r.PoolQuantity);
        Assert.Equal(0m, r.PoolCostUsd);
    }

    [Fact]
    public void QuoteCurrencyIsConvertedToUsd_AndFeesToo()
    {
        var r = Run(Trade(0, OrderSide.Buy, 2m, 100m, fee: 1m, symbol: "ETH/GBP"))["ETH"];
        Assert.Equal(250m, r.PoolCostUsd);   // 200 GBP * 1.25
        Assert.Equal(1.25m, r.FeesUsd);
    }

    [Fact]
    public void AssetsAreTrackedSeparately()
    {
        var all = Run(Trade(0, OrderSide.Buy, 1m, 100m), Trade(0, OrderSide.Buy, 10m, 5m, symbol: "SOL/USD"));
        Assert.Equal(100m, all["XBT"].AverageCostUsd);
        Assert.Equal(5m, all["SOL"].AverageCostUsd);
    }

    [Fact]
    public void ApplyCostBasis_UsesPositionAndDiluteExtraCoinsAtZeroCost()
    {
        var state = new TradingStateService(new DelistedPriceService(new Moq.Mock<Microsoft.Extensions.Logging.ILogger<DelistedPriceService>>().Object));
        state.SetCachedTrades(new[]
        {
            Trade(0, OrderSide.Buy, 1m, 100m, fee: 2m, symbol: "SOLUSD"),
        });

        // 1.5 held: 1 came from trades, 0.5 e.g. a staking reward with no cost
        var bal = new BalanceDto { Asset = "SOL", Total = 1.5m, LatestValue = 300m };
        state.ApplyCostBasis(bal);

        Assert.Equal(100m, bal.TotalCostBasis);
        Assert.Equal(2m, bal.TotalFees);
        Assert.Equal(198m, bal.NetProfitLoss); // 300 - 100 - 2
    }

    [Fact]
    public void ApplyCostBasis_FiatAndUnknownAssets_HaveNoBasis()
    {
        var state = new TradingStateService(new DelistedPriceService(new Moq.Mock<Microsoft.Extensions.Logging.ILogger<DelistedPriceService>>().Object));
        var usd = new BalanceDto { Asset = "USD", Total = 100m, LatestValue = 100m };
        var unknown = new BalanceDto { Asset = "ZZZ", Total = 1m, LatestValue = 1m, TotalCostBasis = 5m };

        state.ApplyCostBasis(usd);
        state.ApplyCostBasis(unknown);

        Assert.Null(usd.TotalCostBasis);
        Assert.Null(unknown.TotalCostBasis); // a stale value is cleared, not left behind
    }
}
