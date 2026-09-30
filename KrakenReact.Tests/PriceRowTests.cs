using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class KlineSnapshotOverloadTests
{
    private static PriceDataItem Item()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = true };
        var start = DateTime.UtcNow.AddDays(-400);
        item.AddKlineHistory(Enumerable.Range(0, 400).Select(i => new DerivedKline
        {
            Interval = "OneDay", OpenTime = start.AddDays(i), Close = 100m + (i % 37) + i / 10m,
            High = 110m, Low = 90m, Open = 100m, Volume = 1000m,
        }).ToList());
        item.SetLiveKline(new DerivedKline { Interval = "OneMinute", OpenTime = DateTime.UtcNow, Close = 143.21m });
        return item;
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(31)] [InlineData(365)]
    public void PassingOneSnapshot_GivesTheSameNumbersAsEachCallTakingItsOwn(int days)
    {
        var item = Item();
        var snap = item.GetKlineSnapshot();

        Assert.Equal(item.ClosePriceDiff(days), item.ClosePriceDiff(days, snap));
        Assert.Equal(item.CloseMovementDiff(days), item.CloseMovementDiff(days, snap));
        Assert.Equal(item.ClosePriceAverage(days), item.ClosePriceAverage(days, snap));
    }

    [Fact]
    public void WeightedPrice_AgeAndPercentage_MatchToo()
    {
        var item = Item();
        var snap = item.GetKlineSnapshot();

        Assert.Equal(item.WeightedPrice, item.WeightedPriceFrom(snap));
        Assert.Equal(item.WeightedPricePercentage, item.WeightedPricePercentageFrom(snap));
        Assert.Equal(item.Age, item.AgeFrom(snap));
        Assert.Equal("Old", item.AgeFrom(snap)); // 400 days of history
    }
}

public class PricesControllerAverageBuyTests
{
    private static (PricesController Controller, TradingStateService State) Make()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        state.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD" };
        state.Symbols["XBT/USDT"] = new KrakenSymbol { WebsocketName = "XBT/USDT", BaseAsset = "XBT", QuoteAsset = "USDT" };
        state.Symbols["SOL/USD"] = new KrakenSymbol { WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD" };
        foreach (var pair in new[] { "XBT/USD", "XBT/USDT", "SOL/USD" })
        {
            var item = new PriceDataItem { Symbol = pair };
            item.TickerData = new TickerDataItem { LastTradePrice = 100m };
            state.Prices[pair] = item;
        }
        var db = new DbMethods(new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<KrakenDbContext>>().Object, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var kraken = new KrakenRestService(db, state, new Mock<ILogger<KrakenRestService>>().Object);
        return (new PricesController(state, kraken, null!, db), state);
    }

    private static void Buy(TradingStateService s, string id, string symbol, decimal avg, string status = "Closed", string side = "Buy") =>
        s.Orders[id] = new OrderDto { Id = id, Symbol = symbol, Side = side, Status = status, AveragePrice = avg, Quantity = 1m };

    private static decimal AvgBuy(ActionResult<List<PriceDto>> result, string displaySymbol) =>
        ((List<PriceDto>)((OkObjectResult)result.Result!).Value!).Single(p => p.DisplaySymbol == displaySymbol).AverageBuyPrice;

    [Fact]
    public void AveragesTheClosedBuysOfThatPair()
    {
        var (c, s) = Make();
        Buy(s, "1", "SOLUSD", 100m); Buy(s, "2", "SOLUSD", 200m);
        Assert.Equal(150m, AvgBuy(c.GetAll(), "SOL/USD"));
    }

    [Fact]
    public void IgnoresSellsAndOrdersThatAreNotClosed()
    {
        var (c, s) = Make();
        Buy(s, "1", "SOLUSD", 100m);
        Buy(s, "2", "SOLUSD", 900m, side: "Sell");
        Buy(s, "3", "SOLUSD", 900m, status: "Open");
        Assert.Equal(100m, AvgBuy(c.GetAll(), "SOL/USD"));
    }

    [Fact]
    public void OrdersInKrakensXbtNaming_CountForTheBtcRow()
    {
        var (c, s) = Make();
        Buy(s, "1", "XBTUSD", 50_000m);   // stored the way Kraken names it
        Assert.Equal(50_000m, AvgBuy(c.GetAll(), "BTC/USD"));
    }

    [Fact]
    public void AUsdtPairIsNotMixedIntoTheUsdPair()
    {
        // "XBTUSD" is a prefix of "XBTUSDT", so the old StartsWith match blended the two
        var (c, s) = Make();
        Buy(s, "1", "XBTUSD", 50_000m);
        Buy(s, "2", "XBTUSDT", 10_000m);

        var result = c.GetAll();
        Assert.Equal(50_000m, AvgBuy(result, "BTC/USD"));
        Assert.Equal(10_000m, AvgBuy(result, "BTC/USDT"));
    }

    [Fact]
    public void NoBuys_MeansZero()
    {
        var (c, _) = Make();
        Assert.Equal(0m, AvgBuy(c.GetAll(), "SOL/USD"));
    }
}
