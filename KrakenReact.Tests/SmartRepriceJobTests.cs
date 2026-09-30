using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

using Placement = KrakenRestService.PlacementResult;

public class SmartRepriceJobTests : OrderJobTestBase
{
    public SmartRepriceJobTests()
    {
        State.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD", PriceDecimals = 1, LotDecimals = 8 };
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        item.TickerData = new TickerDataItem { LastTradePrice = 50_000m };
        State.Prices["XBT/USD"] = item;

        Gateway.Setup(g => g.CancelOrderAsync(It.IsAny<string>())).ReturnsAsync(true);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(true, "NEW1", null));
    }

    private SmartRepriceJob NewJob()
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        clients.Setup(c => c.All).Returns(proxy.Object);
        var hub = new Mock<IHubContext<TradingHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return new SmartRepriceJob(Factory, Gateway.Object, State, Notifier.Object, hub.Object,
            new Mock<ILogger<SmartRepriceJob>>().Object, TestDiagnostics.Create());
    }

    private async Task<AutoRepriceRule> Rule(Action<AutoRepriceRule>? tweak = null)
    {
        var r = new AutoRepriceRule { Symbol = "XBTUSD", MaxDeviationPct = 2m, MinAgeMinutes = 15, RepriceBuys = true };
        tweak?.Invoke(r);
        return await Seed(r);
    }

    private void Open(string id, string side, decimal price, decimal qty = 0.1m, int ageMinutes = 60) =>
        State.Orders[id] = new OrderDto
        {
            Id = id, Symbol = "XBTUSD", Side = side, Status = "Open", Type = "Limit", Price = price, Quantity = qty,
            CreateTime = DateTime.UtcNow.AddMinutes(-ageMinutes),
        };

    private async Task<AutoRepriceRule> Reload(int id)
    {
        await using var db = Factory.CreateDbContext();
        return await db.AutoRepriceRules.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private void NothingCancelledOrPlaced()
    {
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ABuyWellBelowTheMarket_IsCancelledAndReplacedNearIt_KeepingItsSpend()
    {
        await Rule();
        Open("OLD", "Buy", 48_000m);            // 4% below a 50,000 market

        await NewJob().ExecuteAsync(CancellationToken.None);

        Gateway.Verify(g => g.CancelOrderAsync("OLD"), Times.Once);
        // new price = 50,000 * 0.999 = 49,950.0 ; qty keeps the spend: 0.1 * 48,000 / 49,950 = 0.09609609
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Buy, OrderType.Limit, 0.09609609m, 49_950.0m,
            It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
        Assert.Equal("Cancelled", State.Orders["OLD"].Status);
        Assert.Equal(49_950.0m, State.Orders["NEW1"].Price);
    }

    [Fact]
    public async Task ASellKeepsItsQuantity_WhenSellsAreEnabled()
    {
        await Rule(r => { r.RepriceBuys = false; r.RepriceSells = true; });
        Open("OLD", "Sell", 53_000m);           // 6% above the market

        await NewJob().ExecuteAsync(CancellationToken.None);

        // price = 50,000 * 1.001 = 50,050.0 ; the quantity is untouched (the old maths would have inflated it)
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Sell, OrderType.Limit, 0.1m, 50_050.0m,
            It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task AnOrderStillInRange_IsLeftAlone()
    {
        await Rule();
        Open("OK", "Buy", 49_500m);             // 1% below: inside the 2% tolerance
        await NewJob().ExecuteAsync(CancellationToken.None);
        NothingCancelledOrPlaced();
    }

    [Fact]
    public async Task ATooNewOrder_IsLeftAlone()
    {
        await Rule();
        Open("NEW", "Buy", 48_000m, ageMinutes: 5);
        await NewJob().ExecuteAsync(CancellationToken.None);
        NothingCancelledOrPlaced();
    }

    [Fact]
    public async Task SellsAreNotTouched_UnlessTheRuleSaysSo()
    {
        await Rule();                            // RepriceSells defaults to false
        Open("S", "Sell", 53_000m);
        await NewJob().ExecuteAsync(CancellationToken.None);
        NothingCancelledOrPlaced();
    }

    [Fact]
    public async Task ARuleWithNothingEnabled_DoesNothing()
    {
        var rule = await Rule(r => { r.RepriceBuys = false; r.RepriceSells = false; });
        Open("B", "Buy", 48_000m);
        await NewJob().ExecuteAsync(CancellationToken.None);
        NothingCancelledOrPlaced();
        Assert.Contains("neither", (await Reload(rule.Id)).LastResult);
    }

    [Fact]
    public async Task IfTheCancelFails_NothingIsPlaced_SoTheOrderIsNotDuplicated()
    {
        await Rule();
        Open("OLD", "Buy", 48_000m);
        Gateway.Setup(g => g.CancelOrderAsync("OLD")).ReturnsAsync(false);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal("Open", State.Orders["OLD"].Status);
    }

    [Fact]
    public async Task IfTheReplacementIsRejected_TheUserIsToldTheOrderWasCancelledAndNotReplaced()
    {
        var rule = await Rule();
        Open("OLD", "Buy", 48_000m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EOrder:Insufficient funds"));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Notifier.Verify(n => n.Pushover(It.Is<string>(t => t.Contains("FAILED")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.StartsWith("ERROR", (await Reload(rule.Id)).LastResult);
    }

    [Fact]
    public async Task IfTheReplacementOutcomeIsUnknown_TheAlertSaysUnconfirmed()
    {
        await Rule();
        Open("OLD", "Buy", 48_000m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EService:Timeout", Unknown: true));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Notifier.Verify(n => n.Pushover(It.IsAny<string>(), It.Is<string>(t => t.Contains("UNCONFIRMED")), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task StalePriceFeed_SkipsTheRule()
    {
        State.MarkFeedTick(DateTime.UtcNow.AddMinutes(-30));
        var rule = await Rule();
        Open("OLD", "Buy", 48_000m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingCancelledOrPlaced();
        Assert.Contains("price feed", (await Reload(rule.Id)).LastResult);
    }

    [Fact]
    public async Task ATooOldOrder_BeyondMaxAge_IsLeftAlone()
    {
        await Rule(r => r.MaxAgeMinutes = 120);
        Open("ANCIENT", "Buy", 48_000m, ageMinutes: 600);
        await NewJob().ExecuteAsync(CancellationToken.None);
        NothingCancelledOrPlaced();
    }
}
