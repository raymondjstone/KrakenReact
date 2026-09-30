using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class OrderPriceGuardTests
{
    [Theory]
    [InlineData("Buy", 106, 100, true)]     // 6% above the market: fills at once at a bad price
    [InlineData("Buy", 104, 100, false)]    // within tolerance
    [InlineData("Buy", 80, 100, false)]     // a buy well BELOW the market is a normal resting order
    [InlineData("Sell", 94, 100, true)]     // 6% below the market
    [InlineData("Sell", 96, 100, false)]
    [InlineData("Sell", 150, 100, false)]   // a sell well ABOVE the market is a normal resting order
    [InlineData("buy", 106, 100, true)]     // case-insensitive
    public void OnlyOrdersThroughTheMarketAreQuestioned(string side, double price, double market, bool expected)
    {
        Assert.Equal(expected, OrderPriceGuard.IsSuspicious(side, (decimal)price, (decimal)market, 5m));
    }

    [Fact]
    public void ThroughMarketPct_IsSignedByDirection()
    {
        Assert.Equal(10m, OrderPriceGuard.ThroughMarketPct("Buy", 110m, 100m));
        Assert.Equal(-10m, OrderPriceGuard.ThroughMarketPct("Buy", 90m, 100m));
        Assert.Equal(10m, OrderPriceGuard.ThroughMarketPct("Sell", 90m, 100m));
    }

    [Fact]
    public void NoUsableMarketPrice_MeansNoJudgement()
    {
        Assert.Null(OrderPriceGuard.ThroughMarketPct("Buy", 100m, 0m));
        Assert.False(OrderPriceGuard.IsSuspicious("Buy", 1000m, 0m, 5m));
    }

    [Fact]
    public void ZeroDisablesTheGuard()
    {
        Assert.False(OrderPriceGuard.IsSuspicious("Buy", 1000m, 100m, 0m));
    }

    [Fact]
    public void ExactlyAtTheLimit_IsAllowed() =>
        Assert.False(OrderPriceGuard.IsSuspicious("Buy", 105m, 100m, 5m));
}

public class OrdersControllerGuardTests
{
    private static (OrdersController Controller, TradingStateService State) Make()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        state.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD" };
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        item.TickerData = new TickerDataItem { LastTradePrice = 50_000m };
        state.Prices["XBT/USD"] = item;

        var db = new DbMethods(new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<KrakenDbContext>>().Object, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var kraken = new KrakenRestService(db, state, new Mock<ILogger<KrakenRestService>>().Object);
        var hub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<KrakenReact.Server.Hubs.TradingHub>>();
        return (new OrdersController(state, kraken, hub.Object, new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<KrakenDbContext>>().Object), state);
    }

    [Fact]
    public async Task ABuyPricedWellAboveTheMarket_IsRefusedWithAConfirmableError()
    {
        var (controller, _) = Make();

        var result = await controller.Create(new CreateOrderRequest { Symbol = "XBTUSD", Side = "Buy", Price = 60_000m, Quantity = 0.01m });

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(422, refused.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(refused.Value);
        Assert.Contains("PRICE_DEVIATION", json);
        Assert.Contains("20", json);                    // 20% through the market
        Assert.Contains("50000", json);                 // and what the market is
    }

    [Fact]
    public async Task ASellPricedWellBelowTheMarket_IsRefusedToo()
    {
        var (controller, _) = Make();
        var result = await controller.Create(new CreateOrderRequest { Symbol = "XBTUSD", Side = "Sell", Price = 40_000m, Quantity = 0.01m });
        Assert.Equal(422, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task ConfirmedOrder_GetsPastTheGuard()
    {
        var (controller, _) = Make();

        // Past the guard it goes on to call Kraken, which is not reachable in a unit test: any outcome except the guard's 422
        // proves the confirmation was honoured
        var outcome = await Record.ExceptionAsync(async () =>
        {
            var result = await controller.Create(new CreateOrderRequest { Symbol = "XBTUSD", Side = "Buy", Price = 60_000m, Quantity = 0.01m, ConfirmPriceDeviation = true });
            Assert.False(result is ObjectResult { StatusCode: 422 });
        });
        Assert.True(outcome == null || outcome is not Xunit.Sdk.XunitException);
    }

    [Fact]
    public async Task ANormalRestingOrder_IsNotQuestioned()
    {
        var (controller, _) = Make();
        var outcome = await Record.ExceptionAsync(async () =>
        {
            var result = await controller.Create(new CreateOrderRequest { Symbol = "XBTUSD", Side = "Buy", Price = 45_000m, Quantity = 0.01m });
            Assert.False(result is ObjectResult { StatusCode: 422 });
        });
        Assert.True(outcome == null || outcome is not Xunit.Sdk.XunitException);
    }
}
