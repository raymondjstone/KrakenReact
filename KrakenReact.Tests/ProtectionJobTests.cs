using KrakenReact.Server.Data;
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

/// <summary>The stop-loss / take-profit / trailing-stop / profit-ladder job: it sells real holdings, so its decisions are pinned here.</summary>
public class ProtectionJobTests : OrderJobTestBase
{
    public ProtectionJobTests()
    {
        State.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD" };
        State.Symbols["ETH/USD"] = new KrakenSymbol { WebsocketName = "ETH/USD", BaseAsset = "ETH", QuoteAsset = "ZUSD" };
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(true, "O1", null));
    }

    private StopLossTakeProfitJob NewJob() => new(State, Gateway.Object, Notifier.Object, Factory,
        new Mock<ILogger<StopLossTakeProfitJob>>().Object, TestDiagnostics.Create());

    /// <summary>A holding worth `price` per unit with an average cost of 100.</summary>
    private BalanceDto Holding(string asset, decimal price, decimal total = 1m, decimal available = -1m, decimal? cost = 100m)
    {
        var b = new BalanceDto
        {
            Asset = asset, Total = total, Available = available < 0 ? total : available,
            LatestPrice = price, LatestValue = price * total,
            TotalCostBasis = cost is null ? null : cost * total,
        };
        State.Balances[asset] = b;
        return b;
    }

    private void Sold(string symbol, OrderType type, decimal qty, Times times) =>
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(symbol, OrderSide.Sell, type, qty, It.IsAny<decimal>(),
            It.IsAny<string?>(), It.IsAny<bool>()), times);

    private void NothingPlaced() =>
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);

    // ── Stop-loss ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StopLoss_DownMoreThanThreshold_MarketSellsTheAvailableBalance()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        Holding("BTC", price: 90m, total: 2m, available: 1.5m); // -10% from cost 100; 0.5 is locked in another order

        await NewJob().ExecuteAsync(CancellationToken.None);

        Sold("XBTUSD", OrderType.Market, 1.5m, Times.Once());
    }

    [Fact]
    public async Task StopLoss_WithinThreshold_DoesNothing()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        Holding("BTC", price: 97m); // -3%

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingPlaced();
    }

    [Fact]
    public async Task StopLoss_WithoutCostBasis_DoesNothing()
    {
        // The situation the old code got into after every balance refresh: no cost basis, so protection silently off
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        Holding("BTC", price: 50m, cost: null);

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingPlaced();
    }

    [Fact]
    public async Task StopLoss_Disabled_DoesNothing()
    {
        Holding("BTC", price: 10m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingPlaced();
    }

    [Fact]
    public async Task StopLoss_ExcludedAsset_IsNeverSold()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        Holding("BTC", price: 50m);
        await Seed(new AppSettings { Key = StopLossTakeProfitJob.ExcludedAssetsKey, Value = "btc" });

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingPlaced();
    }

    [Fact]
    public async Task StopLoss_DryRun_NotifiesButDoesNotSell()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m; State.DryRunJobs = true;
        Holding("BTC", price: 90m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingPlaced();
        Notifier.Verify(n => n.Pushover(It.Is<string>(t => t.StartsWith("DRY RUN")), It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopLoss_OneAssetThrowing_DoesNotStopTheOthersBeingProtected()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        Holding("BTC", price: 90m);
        Holding("ETH", price: 90m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Sold("ETHUSD", OrderType.Market, 1m, Times.Once());
    }

    [Fact]
    public async Task StopLoss_UnconfirmedSell_SendsAnUnconfirmedAlert()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        Holding("BTC", price: 90m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EService:Timeout", Unknown: true));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Notifier.Verify(n => n.Pushover(It.Is<string>(t => t.Contains("UNCONFIRMED")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // ── Take-profit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TakeProfit_LimitSellsAtMarket_NotPostOnly()
    {
        State.TakeProfitEnabled = true; State.TakeProfitPct = 15m;
        Holding("BTC", price: 120m); // +20%

        await NewJob().ExecuteAsync(CancellationToken.None);

        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Sell, OrderType.Limit, 1m, 120m,
            It.IsAny<string?>(), false), Times.Once);
    }

    // ── Trailing stop ───────────────────────────────────────────────────────

    [Fact]
    public async Task TrailingStop_SellsAfterAFallFromTheHigh()
    {
        State.TrailingStopEnabled = true; State.TrailingStopPct = 5m;
        var job = NewJob();

        Holding("BTC", price: 100m);
        await job.ExecuteAsync(CancellationToken.None);   // records the high
        NothingPlaced();

        Holding("BTC", price: 94m);                        // -6% from the high
        await job.ExecuteAsync(CancellationToken.None);

        Sold("XBTUSD", OrderType.Market, 1m, Times.Once());
        Assert.False(State.TrailingHighPrices.ContainsKey("BTC")); // reset after selling
    }

    [Fact]
    public async Task TrailingStop_HighIsForgottenWhenThePositionIsClosed_SoARebuyIsNotInstantlyStopped()
    {
        State.TrailingStopEnabled = true; State.TrailingStopPct = 5m;
        var job = NewJob();

        Holding("BTC", price: 100m);
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(100m, State.TrailingHighPrices["BTC"]);

        Holding("BTC", price: 100m, total: 0m);            // sold by hand
        await job.ExecuteAsync(CancellationToken.None);
        Assert.False(State.TrailingHighPrices.ContainsKey("BTC"));

        Holding("BTC", price: 60m);                        // bought again far lower
        await job.ExecuteAsync(CancellationToken.None);

        NothingPlaced(); // the old high (100) must not make 60 look like a 40% collapse
    }

    [Fact]
    public async Task TrailingStop_CoinsRestingInASellOrderStillCountAsHeld()
    {
        State.TrailingStopEnabled = true; State.TrailingStopPct = 5m;
        var job = NewJob();

        Holding("BTC", price: 100m);
        await job.ExecuteAsync(CancellationToken.None);

        Holding("BTC", price: 100m, total: 1m, available: 0m); // everything locked in a sell order
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(100m, State.TrailingHighPrices["BTC"]); // not forgotten
    }

    // ── Profit ladder ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProfitLadder_SellsItsShareOnceThenWaitsOutTheCooldown()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m, CooldownHours = 24 });
        Holding("BTC", price: 120m, total: 2m); // +20%
        var job = NewJob();

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Sell, OrderType.Limit, 1m, 120m,
            It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task ProfitLadder_BelowTrigger_DoesNothing()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m });
        Holding("BTC", price: 105m); // +5%

        await NewJob().ExecuteAsync(CancellationToken.None);

        NothingPlaced();
    }

    // ── Profit ladder: one sale per crossing ────────────────────────────────

    private void LadderSold(Times times) =>
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Sell, OrderType.Limit, It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<string?>(), false), times);

    [Fact]
    public async Task ProfitLadder_DoesNotSellAgainWhileTheGainStaysAboveTheTrigger_EvenWithNoCooldown()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m, CooldownHours = 0 });
        Holding("BTC", price: 120m, total: 2m);
        var job = NewJob();

        for (var i = 0; i < 4; i++) await job.ExecuteAsync(CancellationToken.None);

        LadderSold(Times.Once()); // previously: one more slice every cooldown period for as long as the price stayed high
    }

    [Fact]
    public async Task ProfitLadder_ReArmsAfterTheGainFallsBelowTheTrigger_ThenSellsAtTheNextCrossing()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m, CooldownHours = 0 });
        var job = NewJob();

        Holding("BTC", price: 120m, total: 2m);
        await job.ExecuteAsync(CancellationToken.None);          // first crossing → sells
        Holding("BTC", price: 105m, total: 1m);                  // falls back to +5% → re-arms
        await job.ExecuteAsync(CancellationToken.None);
        LadderSold(Times.Once());

        Holding("BTC", price: 125m, total: 1m);                  // crosses again → sells again
        await job.ExecuteAsync(CancellationToken.None);
        LadderSold(Times.Exactly(2));
    }

    [Fact]
    public async Task ProfitLadder_ADipDuringTheCooldownStillReArms()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m, CooldownHours = 24 });
        var job = NewJob();

        Holding("BTC", price: 120m, total: 2m);
        await job.ExecuteAsync(CancellationToken.None);
        Holding("BTC", price: 102m, total: 1m);                  // dips while the 24h cooldown is still running
        await job.ExecuteAsync(CancellationToken.None);

        // Force the cooldown to have elapsed, then cross again
        await using (var db = Factory.CreateDbContext())
        {
            var rule = await db.ProfitLadderRules.SingleAsync();
            rule.LastTriggeredAt = DateTime.UtcNow.AddHours(-25);
            await db.SaveChangesAsync();
        }
        Holding("BTC", price: 130m, total: 1m);
        await job.ExecuteAsync(CancellationToken.None);

        LadderSold(Times.Exactly(2));
    }

    [Fact]
    public async Task ProfitLadder_AFailedSellStaysArmed_AndIsRetried()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m, CooldownHours = 0 });
        Holding("BTC", price: 120m, total: 2m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EOrder:Insufficient funds"));
        var job = NewJob();

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        LadderSold(Times.Exactly(2));
    }

    [Fact]
    public async Task ProfitLadder_AnUnconfirmedSellIsNotRepeated()
    {
        await Seed(new ProfitLadderRule { Symbol = "BTC/USD", TriggerPct = 10m, SellPct = 50m, CooldownHours = 0 });
        Holding("BTC", price: 120m, total: 2m);
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(false, null, "EService:Timeout", Unknown: true));
        var job = NewJob();

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        LadderSold(Times.Once());
    }

    [Fact]
    public void DisarmedRules_RoundTrip_AndIgnoreJunk()
    {
        var ids = StopLossTakeProfitJob.ParseDisarmedRules(" 3, 1 ,x,,-2");
        Assert.Equal("1,3", StopLossTakeProfitJob.SerializeDisarmedRules(ids));
        Assert.Empty(StopLossTakeProfitJob.ParseDisarmedRules(null));
    }
}

/// <summary>Nothing that trades may act on a price once the live feed has gone quiet.</summary>
public class FeedStalenessTests : OrderJobTestBase
{
    public FeedStalenessTests()
    {
        State.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD", PriceDecimals = 1, LotDecimals = 8, OrderMin = 0.0001m, MinValue = 1m };
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new KrakenRestService.PlacementResult(true, "O1", null));
        Gateway.Setup(g => g.PlaceOrderWithUserRefAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new KrakenRestService.PlacementResult(true, "O1", null));
    }

    private void FeedDiedAMinutesAgo() => State.MarkFeedTick(DateTime.UtcNow.AddMinutes(-30));

    private void NothingPlaced()
    {
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        Gateway.Verify(g => g.PlaceOrderWithUserRefAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void FeedState_NeverTicked_TickedRecently_AndTickedLongAgo()
    {
        var fresh = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        Assert.False(fresh.IsPriceFeedAlive());   // nothing is known yet
        Assert.Null(fresh.FeedAge);

        fresh.MarkFeedTick();
        Assert.True(fresh.IsPriceFeedAlive());

        fresh.MarkFeedTick(DateTime.UtcNow.AddMinutes(-6));
        Assert.False(fresh.IsPriceFeedAlive());
        Assert.True(fresh.IsPriceFeedAlive(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task StopLoss_DoesNotSellOnAStalePrice()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        State.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1m, Available = 1m, LatestPrice = 50m, LatestValue = 50m, TotalCostBasis = 100m };
        FeedDiedAMinutesAgo();

        await new StopLossTakeProfitJob(State, Gateway.Object, Notifier.Object, Factory,
            new Mock<ILogger<StopLossTakeProfitJob>>().Object, TestDiagnostics.Create()).ExecuteAsync(CancellationToken.None);

        NothingPlaced();
    }

    [Fact]
    public async Task StopLoss_ResumesWhenTheFeedRecovers()
    {
        State.StopLossEnabled = true; State.StopLossPct = 5m;
        State.Balances["BTC"] = new BalanceDto { Asset = "BTC", Total = 1m, Available = 1m, LatestPrice = 50m, LatestValue = 50m, TotalCostBasis = 100m };
        var job = new StopLossTakeProfitJob(State, Gateway.Object, Notifier.Object, Factory,
            new Mock<ILogger<StopLossTakeProfitJob>>().Object, TestDiagnostics.Create());

        FeedDiedAMinutesAgo();
        await job.ExecuteAsync(CancellationToken.None);
        NothingPlaced();

        State.MarkFeedTick(); // ticks are flowing again
        await job.ExecuteAsync(CancellationToken.None);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBTUSD", OrderSide.Sell, OrderType.Market, 1m, 0m, It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task MicroTrade_DoesNotBuyOnAStalePrice()
    {
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        item.TickerData = new TickerDataItem { LastTradePrice = 50_000m, ChangePct24h = -9m };
        State.Prices["XBT/USD"] = item;
        State.Balances["USD"] = new BalanceDto { Asset = "USD", Total = 10_000m, Available = 10_000m };
        var rule = await Seed(new MicroTradeRule { Symbol = "XBT/USD", DropPct = 5m, DropIntervalHours = 24, RisePct = 10m, BuyOrderTotal = 100m, MaxOrdersPerWindow = 2, WindowHours = 2, CooldownHours = 1 });
        FeedDiedAMinutesAgo();

        var db = new DbMethods(new Mock<IDbContextFactory<KrakenDbContext>>().Object, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var pc = new PriceChangeService(new KrakenRestService(db, State, new Mock<ILogger<KrakenRestService>>().Object), State, new Mock<ILogger<PriceChangeService>>().Object);
        await new MicroTradeJob(Factory, Gateway.Object, State, pc, Notifier.Object, new Mock<ILogger<MicroTradeJob>>().Object, TestDiagnostics.Create())
            .ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NothingPlaced();
        await using var ctx = Factory.CreateDbContext();
        Assert.Contains("feed", (await ctx.MicroTradeRules.AsNoTracking().SingleAsync()).LastResult);
        Assert.Empty(await ctx.MicroTradeOrders.ToListAsync());
    }

    [Fact]
    public async Task Bracket_DoesNotTriggerItsStopOnAStalePrice()
    {
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        item.TickerData = new TickerDataItem { LastTradePrice = 50m };  // far below the 90 stop
        State.Prices["XBT/USD"] = item;
        await Seed(new BracketOrder
        {
            KrakenOrderId = "PARENT", Symbol = "XBT/USD", Side = "Buy", Quantity = 1m, EntryPrice = 100m, StopPrice = 90m,
            TakeProfitPrice = 120m, TakeProfitOrderId = "TP1", Status = "Active",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10), ActivatedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        Gateway.Setup(g => g.GetOrderInfoAsync("TP1")).ReturnsAsync(new CombinedOrder { Id = "TP1", Status = OrderStatus.Open });
        FeedDiedAMinutesAgo();

        await new BracketMonitorJob(Factory, Gateway.Object, State, Notifier.Object,
            new Mock<ILogger<BracketMonitorJob>>().Object, TestDiagnostics.Create()).ExecuteAsync(CancellationToken.None);

        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
        NothingPlaced();
    }
}
