using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

using Placement = KrakenRestService.PlacementResult;

public class DcaJobTests : OrderJobTestBase
{
    public DcaJobTests()
    {
        State.Symbols["SOL/USD"] = new KrakenSymbol
        {
            WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD",
            PriceDecimals = 2, LotDecimals = 4, OrderMin = 0.01m, MinValue = 5m,
        };
        SetPrice(100m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(true, "O1", null));
    }

    private DcaJob NewJob() => new(Factory, Gateway.Object, State, Notifier.Object, new Mock<ILogger<DcaJob>>().Object);

    private void SetPrice(decimal price)
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.TickerData = new TickerDataItem { LastTradePrice = price };
        State.Prices["SOL/USD"] = item;
    }

    private async Task<DcaRule> Rule(Action<DcaRule>? tweak = null)
    {
        var r = new DcaRule { Symbol = "SOL/USD", AmountUsd = 50m };
        tweak?.Invoke(r);
        return await Seed(r);
    }

    private async Task<DcaRule> Reload(int id)
    {
        await using var db = Factory.CreateDbContext();
        return await db.DcaRules.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private void NothingPlaced() =>
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);

    [Fact]
    public async Task BuysJustAboveMarket_AsAnImmediateLimit_NotPostOnly()
    {
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        // 100 * 1.002 = 100.20 ; 50 / 100.20 = 0.4990 (floored to the pair lot precision of 4 decimals)
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("SOL/USD", OrderSide.Buy, OrderType.Limit, 0.499m, 100.20m,
            It.IsAny<string?>(), false), Times.Once);
        Assert.StartsWith("OK", (await Reload(rule.Id)).LastRunResult);
    }

    [Fact]
    public async Task TheQuantityNeverBuysMoreThanTheBudget()
    {
        SetPrice(3m);                         // 50 / 3.006 = 16.6334... floored, never rounded up
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("SOL/USD", OrderSide.Buy, OrderType.Limit,
            It.Is<decimal>(q => q * 3.01m <= 50m + 0.01m && q == Math.Floor(q * 10000m) / 10000m), It.IsAny<decimal>(), It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task ACheapCoin_IsPricedAtThePairPrecision_NotRoundedToTwoDecimals()
    {
        State.Symbols["SOL/USD"] = new KrakenSymbol { WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD", PriceDecimals = 5, LotDecimals = 0, OrderMin = 1m, MinValue = 1m };
        SetPrice(0.0456m);
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        // 0.0456 * 1.002 = 0.04569 ; a fixed 2-decimal round would have made it 0.05
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("SOL/USD", OrderSide.Buy, OrderType.Limit, It.IsAny<decimal>(), 0.04569m,
            It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task InactiveRule_DoesNothing()
    {
        var rule = await Rule(r => r.Active = false);
        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);
        NothingPlaced();
    }

    [Fact]
    public async Task StalePriceFeed_SkipsRatherThanBuyingAtAnOldPrice()
    {
        State.MarkFeedTick(DateTime.UtcNow.AddMinutes(-30));
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        NothingPlaced();
        Assert.Contains("price feed", (await Reload(rule.Id)).LastRunResult);
    }

    [Fact]
    public async Task ABudgetBelowTheMinimumOrder_IsSkippedWithAReason()
    {
        var rule = await Rule(r => r.AmountUsd = 1m);   // 0.00998 SOL: under the 0.01 minimum
        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        NothingPlaced();
        Assert.Contains("below min", (await Reload(rule.Id)).LastRunResult);
    }

    [Fact]
    public async Task MovingAverageFilter_SkipsWhenThePriceIsAboveTheAverage()
    {
        var item = State.Prices["SOL/USD"];
        item.AddKlineHistory(Enumerable.Range(1, 30).Select(i => new DerivedKline
        {
            Interval = "OneDay", OpenTime = DateTime.UtcNow.AddDays(-i), Close = 80m,
        }).ToList());                                    // 20-day average = 80, price is 100.2
        var rule = await Rule(r => { r.ConditionalEnabled = true; r.ConditionalMaPeriod = 20; });

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        NothingPlaced();
        Assert.Contains("Conditional skip", (await Reload(rule.Id)).LastRunResult);
    }

    [Fact]
    public async Task MovingAverageFilter_BuysWhenThePriceIsBelowTheAverage()
    {
        var item = State.Prices["SOL/USD"];
        item.AddKlineHistory(Enumerable.Range(1, 30).Select(i => new DerivedKline
        {
            Interval = "OneDay", OpenTime = DateTime.UtcNow.AddDays(-i), Close = 130m,
        }).ToList());
        var rule = await Rule(r => { r.ConditionalEnabled = true; r.ConditionalMaPeriod = 20; });

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("SOL/USD", OrderSide.Buy, OrderType.Limit, It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task DryRun_NotifiesAndPlacesNothing()
    {
        State.DryRunJobs = true;
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        NothingPlaced();
        Assert.StartsWith("DRY RUN", (await Reload(rule.Id)).LastRunResult);
    }

    [Fact]
    public async Task ARejection_IsRecordedOnTheRule()
    {
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EOrder:Insufficient funds"));
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        Assert.Contains("Insufficient funds", (await Reload(rule.Id)).LastRunResult);
    }

    [Fact]
    public async Task AnUnconfirmedOrder_IsFlagged_AndAlertedAbout()
    {
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EService:Timeout", Unknown: true));
        var rule = await Rule();

        await NewJob().ExecuteAsync(rule.Id, CancellationToken.None);

        Assert.StartsWith("UNCONFIRMED", (await Reload(rule.Id)).LastRunResult);
        Notifier.Verify(n => n.Pushover(It.Is<string>(t => t.Contains("Unconfirmed")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AnExceptionFromThePlacement_IsCaughtAndRecorded_NotThrown()
    {
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var rule = await Rule();

        var ex = await Record.ExceptionAsync(() => NewJob().ExecuteAsync(rule.Id, CancellationToken.None));

        Assert.Null(ex); // a throw here would make Hangfire treat the run as failed
        Assert.Contains("connection reset", (await Reload(rule.Id)).LastRunResult);
    }
}
