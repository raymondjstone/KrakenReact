using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

using Placement = KrakenRestService.PlacementResult;

public class RebalanceJobTests : OrderJobTestBase
{
    public RebalanceJobTests()
    {
        State.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD" };
        State.Symbols["ETH/USD"] = new KrakenSymbol { WebsocketName = "ETH/USD", BaseAsset = "ETH", QuoteAsset = "ZUSD" };
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(true, "O1", null));
    }

    private RebalanceJob NewJob() => new(State, Gateway.Object, Notifier.Object, Factory, new Mock<ILogger<RebalanceJob>>().Object);

    private void Balance(string asset, decimal total, decimal price, decimal? available = null) =>
        State.Balances[asset] = new BalanceDto
        {
            Asset = asset, Total = total, Available = available ?? total, LatestPrice = price, LatestValue = total * price,
        };

    private async Task<RebalanceSchedule> Schedule(string targets, bool auto = true, decimal drift = 5m) =>
        await Seed(new RebalanceSchedule { Targets = targets, AutoExecute = auto, DriftMinPct = drift });

    private async Task<RebalanceSchedule> Reload(int id)
    {
        await using var db = Factory.CreateDbContext();
        return await db.RebalanceSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private void Placed(string symbol, OrderSide side, decimal qty, Times times) =>
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(symbol, side, OrderType.Limit, qty, It.IsAny<decimal>(),
            It.IsAny<string?>(), false), times);

    private void NothingPlaced() =>
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);

    [Fact]
    public async Task WithinTheDriftThreshold_NothingHappens()
    {
        Balance("BTC", 1m, 50m); Balance("ETH", 1m, 50m);           // 50 / 50
        var s = await Schedule("BTC:52,ETH:48", drift: 5m);          // 2 points out

        await NewJob().ExecuteAsync(s.Id);

        NothingPlaced();
        Assert.Contains("no action", (await Reload(s.Id)).LastRunResult);
    }

    [Fact]
    public async Task OutsideTheThreshold_SellsTheExcess_AndCapsTheBuyToFreeCash()
    {
        Balance("BTC", 2m, 100m); Balance("ETH", 0m + 0.5m, 100m);   // 200 : 50 → 80% / 20%
        Balance("USD", 0m + 0.01m, 1m);
        var s = await Schedule("BTC:50,ETH:50", drift: 5m);

        await NewJob().ExecuteAsync(s.Id);

        // Total ≈ 250: BTC target ≈125 → sell ≈0.75; ETH wants ≈0.75 more but only ~$0.01 is free
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Sell, OrderType.Limit,
            It.Is<decimal>(q => Math.Abs(q - 0.75m) < 0.01m), It.IsAny<decimal>(), It.IsAny<string?>(), false), Times.Once);
        Placed("ETHUSD", OrderSide.Buy, 0.0001m, Times.Once());   // the buy is cut down to the ~$0.01 that is free, not sent in full
    }

    [Fact]
    public async Task SellsAreSentBeforeBuys()
    {
        Balance("BTC", 5m, 100m); Balance("ETH", 0.5m, 100m); Balance("USD", 200m, 1m);   // 500 / 50 / 200 of 750
        var s = await Schedule("BTC:40,ETH:60,USD:0", drift: 5m);                          // BTC is over-weight, ETH under
        var order = new List<OrderSide>();
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, OrderSide, OrderType, decimal, decimal, string?, bool>((_, side, _, _, _, _, _) => order.Add(side))
            .ReturnsAsync(new Placement(true, "O", null));

        await NewJob().ExecuteAsync(s.Id);

        Assert.Equal(new[] { OrderSide.Sell, OrderSide.Buy }, order);
    }

    [Fact]
    public async Task ACashTarget_IsNotTreatedAsSomethingToTrade()
    {
        Balance("BTC", 1m, 100m); Balance("USD", 100m, 1m);
        var s = await Schedule("BTC:50,USD:50", drift: 5m);           // already 50/50

        await NewJob().ExecuteAsync(s.Id);

        NothingPlaced();
        Assert.DoesNotContain("no symbol", (await Reload(s.Id)).LastRunResult); // used to error "USD: no symbol found" every run
    }

    [Fact]
    public async Task ASellIsCappedToWhatIsNotAlreadyInOpenOrders()
    {
        Balance("BTC", 2m, 100m, available: 0.25m);                   // 1.75 is locked in another order
        Balance("ETH", 0.5m, 100m);
        var s = await Schedule("BTC:50,ETH:50", drift: 5m);

        await NewJob().ExecuteAsync(s.Id);

        Placed("XBTUSD", OrderSide.Sell, 0.25m, Times.Once());
    }

    [Fact]
    public async Task ABuyIsCappedToTheCashThatIsFree()
    {
        Balance("BTC", 1m, 100m); Balance("ETH", 0.5m, 100m); Balance("USD", 20m, 1m, available: 20m);
        var s = await Schedule("BTC:30,ETH:70,USD:0", drift: 5m);

        await NewJob().ExecuteAsync(s.Id);

        // wants far more ETH than $20 buys; it spends the $20 and no more (0.2 ETH at 100)
        Placed("ETHUSD", OrderSide.Buy, 0.2m, Times.Once());
    }

    [Fact]
    public async Task AlertOnlySchedule_SendsANotificationAndPlacesNothing()
    {
        Balance("BTC", 2m, 100m); Balance("ETH", 0.5m, 100m);
        var s = await Schedule("BTC:50,ETH:50", auto: false);

        await NewJob().ExecuteAsync(s.Id);

        NothingPlaced();
        Notifier.Verify(n => n.Pushover(It.Is<string>(t => t.Contains("Rebalance Alert")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DryRun_PlacesNothing()
    {
        State.DryRunJobs = true;
        Balance("BTC", 2m, 100m); Balance("ETH", 0.5m, 100m);
        var s = await Schedule("BTC:50,ETH:50");

        await NewJob().ExecuteAsync(s.Id);

        NothingPlaced();
    }

    [Fact]
    public async Task StalePriceFeed_SkipsTheRun()
    {
        State.MarkFeedTick(DateTime.UtcNow.AddMinutes(-30));
        Balance("BTC", 2m, 100m); Balance("ETH", 0.5m, 100m);
        var s = await Schedule("BTC:50,ETH:50");

        await NewJob().ExecuteAsync(s.Id);

        NothingPlaced();
        Assert.Contains("price feed", (await Reload(s.Id)).LastRunResult);
    }

    [Fact]
    public async Task AnUnconfirmedOrder_IsReportedAsSuch_NotAsSuccess()
    {
        Balance("BTC", 2m, 100m); Balance("ETH", 0.5m, 100m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EService:Timeout", Unknown: true));
        var s = await Schedule("BTC:50,ETH:50");

        await NewJob().ExecuteAsync(s.Id);

        var result = (await Reload(s.Id)).LastRunResult;
        Assert.StartsWith("Partial", result);
        Assert.Contains("UNCONFIRMED", result);
    }

    [Fact]
    public async Task InactiveSchedule_DoesNothing()
    {
        Balance("BTC", 2m, 100m); Balance("ETH", 0.5m, 100m);
        var s = await Seed(new RebalanceSchedule { Targets = "BTC:50,ETH:50", AutoExecute = true, Active = false });

        await NewJob().ExecuteAsync(s.Id);

        NothingPlaced();
    }
}
