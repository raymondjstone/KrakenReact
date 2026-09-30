using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class BracketMonitorTests
{
    [Theory]
    [InlineData("Buy", 94.9, 95, true)]    // long: price fell through the stop
    [InlineData("Buy", 95.0, 95, true)]    // exactly at the stop
    [InlineData("Buy", 100, 95, false)]
    [InlineData("Sell", 105.1, 105, true)] // short: price rose through the stop
    [InlineData("Sell", 100, 105, false)]
    [InlineData("Buy", 90, 0, false)]      // no stop configured
    [InlineData("Buy", 0, 95, false)]      // no price yet
    public void IsStopHit_IsSideAware(string side, double price, double stop, bool expected)
    {
        Assert.Equal(expected, BracketMonitorJob.IsStopHit(side, (decimal)price, (decimal)stop));
    }
}

public class SmartRepriceQuantityTests
{
    [Fact]
    public void Buy_KeepsTotalSpend_AndNeverExceedsIt()
    {
        // 10 coins @ 100 = 1000 spend; new price 110 -> 9.09090909 coins, floored at 8 dp
        var qty = SmartRepriceJob.ComputeRepriceQuantity(true, 10m, 100m, 110m, 8);
        Assert.Equal(9.09090909m, qty);
        Assert.True(qty * 110m <= 1000m);
    }

    [Fact]
    public void Sell_KeepsItsQuantity_WhateverThePriceDoes()
    {
        // The old logic would have turned 10 into 11.11 when the price fell 100 -> 90
        Assert.Equal(10m, SmartRepriceJob.ComputeRepriceQuantity(false, 10m, 100m, 90m, 8));
        Assert.Equal(10m, SmartRepriceJob.ComputeRepriceQuantity(false, 10m, 100m, 110m, 8));
    }

    [Fact]
    public void Quantity_IsFlooredToLotPrecision()
    {
        Assert.Equal(1.23m, SmartRepriceJob.ComputeRepriceQuantity(false, 1.239m, 5m, 5m, 2));
    }
}

public class ProtectionSettingsTests
{
    [Fact]
    public void ParseExcludedAssets_NormalizesAndIgnoresCase()
    {
        var set = StopLossTakeProfitJob.ParseExcludedAssets(" btc , XXBT,eth.F ,");
        Assert.Contains("BTC", set);
        Assert.Contains("ETH", set);
        Assert.Equal(2, set.Count); // XXBT and btc are the same asset once normalized
    }

    [Fact]
    public void ParseExcludedAssets_EmptyOrNull_IsEmpty()
    {
        Assert.Empty(StopLossTakeProfitJob.ParseExcludedAssets(null));
        Assert.Empty(StopLossTakeProfitJob.ParseExcludedAssets("  "));
    }

    [Fact]
    public void TrailingHighs_RoundTrip()
    {
        var json = StopLossTakeProfitJob.SerializeTrailingHighs(new Dictionary<string, decimal> { ["SOL"] = 152.5m, ["BTC"] = 98000m });
        var back = StopLossTakeProfitJob.ParseTrailingHighs(json);
        Assert.Equal(152.5m, back["SOL"]);
        Assert.Equal(98000m, back["BTC"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void ParseTrailingHighs_BadInput_IsEmptyNotAnException(string? json)
    {
        Assert.Empty(StopLossTakeProfitJob.ParseTrailingHighs(json));
    }
}

public class TrailingHighLifecycleTests
{
    [Theory]
    [InlineData(1.5, 100, true)]
    [InlineData(0, 100, false)]    // sold everything
    [InlineData(0.001, 2, false)]  // dust
    [InlineData(1, 5, true)]       // boundary counts as held
    public void IsPositionHeld(double total, double valueUsd, bool expected)
    {
        Assert.Equal(expected, StopLossTakeProfitJob.IsPositionHeld((decimal)total, (decimal)valueUsd));
    }
}
