using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

using Placement = KrakenRestService.PlacementResult;

/// <summary>
/// MicroTradeJob end to end against a fake exchange: when it buys, what it records before and after talking to Kraken,
/// how it sells, and what it does when the exchange's answer is lost or contradicts its own cache.
/// </summary>
public class MicroTradeJobTests : OrderJobTestBase
{
    private const string Pair = "XBT/USD";

    public MicroTradeJobTests()
    {
        State.Symbols[Pair] = new KrakenSymbol
        {
            WebsocketName = Pair, BaseAsset = "XBT", QuoteAsset = "ZUSD",
            PriceDecimals = 1, LotDecimals = 8, OrderMin = 0.0001m, MinValue = 1m,
        };
        State.Balances["USD"] = new BalanceDto { Asset = "USD", Total = 10_000m, Available = 10_000m };
        SetMarket(price: 50_000m, change24h: -6m);
    }

    private MicroTradeJob NewJob()
    {
        var db = new DbMethods(new Mock<IDbContextFactory<KrakenDbContext>>().Object, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var restForPriceChange = new KrakenRestService(db, State, new Mock<ILogger<KrakenRestService>>().Object);
        var priceChange = new PriceChangeService(restForPriceChange, State, new Mock<ILogger<PriceChangeService>>().Object);
        return new MicroTradeJob(Factory, Gateway.Object, State, priceChange, Notifier.Object,
            new Mock<ILogger<MicroTradeJob>>().Object, TestDiagnostics.Create());
    }

    private void SetMarket(decimal price, decimal change24h)
    {
        var item = new PriceDataItem { Symbol = Pair };
        item.TickerData = new TickerDataItem { LastTradePrice = price, ChangePct24h = change24h };
        State.Prices[Pair] = item;
    }

    private static MicroTradeRule Rule() => new()
    {
        Symbol = Pair, DropPct = 5m, DropIntervalHours = 24, RisePct = 10m, BuyOrderTotal = 100m,
        MaxOrdersPerWindow = 2, WindowHours = 2, CooldownHours = 1, Active = true,
    };

    private async Task<List<MicroTradeOrder>> Orders()
    {
        await using var db = Factory.CreateDbContext();
        return await db.MicroTradeOrders.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
    }

    private async Task<MicroTradeRule> ReloadRule(int id)
    {
        await using var db = Factory.CreateDbContext();
        return await db.MicroTradeRules.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private void BuyPlacementReturns(Placement result) =>
        Gateway.Setup(g => g.PlaceOrderWithUserRefAsync(It.IsAny<string>(), OrderSide.Buy, It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(result);

    private void SellPlacementReturns(Placement result) =>
        Gateway.Setup(g => g.PlaceOrderWithUserRefAsync(It.IsAny<string>(), OrderSide.Sell, It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(result);

    private void NoPlacements() =>
        Gateway.Verify(g => g.PlaceOrderWithUserRefAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);

    private void KrakenOrder(string id, OrderStatus status, decimal filled, decimal qty = 0.002m) =>
        Gateway.Setup(g => g.GetOrderInfoAsync(id)).ReturnsAsync(new CombinedOrder { Id = id, Status = status, QuantityFilled = filled, Quantity = qty });

    private async Task<MicroTradeOrder> SeedOrder(string status, Action<MicroTradeOrder>? tweak = null, MicroTradeRule? rule = null)
    {
        // Inactive by default so the scheduled tick only exercises the fill monitor, not new buys
        if (rule == null) { var inactive = Rule(); inactive.Active = false; rule = await Seed(inactive); }
        var o = new MicroTradeOrder
        {
            RuleId = rule.Id, Symbol = Pair, Status = status, BuyPrice = 49_950m, Quantity = 0.002m,
            BuyOrderId = "B1", CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        tweak?.Invoke(o);
        return await Seed(o);
    }

    // ── Buying ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoTrigger_WhenTheDropIsNotBigEnough()
    {
        SetMarket(50_000m, change24h: -2m); // needs <= -5
        var rule = await Seed(Rule());

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NoPlacements();
        Assert.Empty(await Orders());
        Assert.Contains("No trigger", (await ReloadRule(rule.Id)).LastResult);
    }

    [Fact]
    public async Task Trigger_PlacesALimitBuyJustUnderMarket_AndRecordsIt()
    {
        var rule = await Seed(Rule());
        uint sentRef = 0;
        BuyPlacementReturns(new Placement(true, "B1", null));
        Gateway.Setup(g => g.PlaceOrderWithUserRefAsync(Pair, OrderSide.Buy, OrderType.Limit, 0.002002m, 49_950.0m, It.IsAny<uint>(), It.IsAny<string?>(), true))
            .Callback<string, OrderSide, OrderType, decimal, decimal, uint, string?, bool>((_, _, _, _, _, r, _, _) => sentRef = r)
            .ReturnsAsync(new Placement(true, "B1", null));

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.Equal("Buying", order.Status);
        Assert.Equal("B1", order.BuyOrderId);
        Assert.Equal(49_950.0m, order.BuyPrice);
        Assert.Equal(0.002002m, order.Quantity); // 100 / 49,950 floored to 8dp, never rounded up
        Assert.True(sentRef > 0);
        Assert.Equal((long)sentRef, order.BuyUserRef); // the userref actually sent is the one stored
    }

    [Fact]
    public async Task TheIntentIsSavedBeforeTheOrderIsSent()
    {
        var rule = await Seed(Rule());
        var rowsWhenSent = -1;
        Gateway.Setup(g => g.PlaceOrderWithUserRefAsync(It.IsAny<string>(), OrderSide.Buy, It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(async () =>
            {
                // At the moment Kraken is being called, a "Placing" row must already be durable
                await using var db = Factory.CreateDbContext();
                rowsWhenSent = await db.MicroTradeOrders.CountAsync(o => o.Status == "Placing");
                return new Placement(true, "B1", null);
            });

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        Assert.Equal(1, rowsWhenSent);
    }

    [Fact]
    public async Task EmergencyStop_BlocksNewBuys()
    {
        var rule = await Seed(Rule());
        await Seed(new AppSettings { Key = MicroTradeJob.EmergencyStopKey, Value = "true" });

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NoPlacements();
        Assert.Contains("EMERGENCY STOP", (await ReloadRule(rule.Id)).LastResult);
    }

    [Fact]
    public async Task InsufficientQuoteBalance_SkipsTheBuy()
    {
        State.Balances["USD"].Available = 10m; // needs ~100
        var rule = await Seed(Rule());

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NoPlacements();
        Assert.Empty(await Orders());
        Assert.Contains("insufficient", (await ReloadRule(rule.Id)).LastResult);
    }

    [Fact]
    public async Task DryRun_RecordsASimulatedOrder_WithoutContactingKraken()
    {
        var rule = Rule(); rule.DryRun = true;
        rule = await Seed(rule);

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NoPlacements();
        Assert.Equal("DryRun", Assert.Single(await Orders()).Status);
    }

    [Fact]
    public async Task Cooldown_BlocksAnotherBuyOnThePair()
    {
        var rule = await Seed(Rule()); // cooldown 1h
        await Seed(new MicroTradeOrder { RuleId = rule.Id, Symbol = Pair, Status = "Buying", CreatedAt = DateTime.UtcNow.AddMinutes(-10) });

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NoPlacements();
        Assert.Contains("cooldown", (await ReloadRule(rule.Id)).LastResult);
    }

    [Fact]
    public async Task RateLimit_StopsBuysOnceTheWindowIsFull()
    {
        var r = Rule(); r.CooldownHours = 0; r.MaxOrdersPerWindow = 1;
        var rule = await Seed(r);
        await Seed(new MicroTradeOrder { RuleId = rule.Id, Symbol = Pair, Status = "Buying", CreatedAt = DateTime.UtcNow.AddMinutes(-30) });

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        NoPlacements();
        Assert.Contains("rate limit", (await ReloadRule(rule.Id)).LastResult);
    }

    [Fact]
    public async Task UnknownOutcome_KeepsThePlacingRow_SoItCountsAndCanBeResolved()
    {
        var rule = await Seed(Rule());
        BuyPlacementReturns(new Placement(false, null, "Request timed out", Unknown: true));

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.Equal("Placing", order.Status);
        Assert.NotNull(order.BuyUserRef);
        Assert.Contains("unconfirmed", order.Note);
    }

    [Fact]
    public async Task DefiniteRejection_LeavesNoRecordOfAnOrder()
    {
        var rule = await Seed(Rule());
        BuyPlacementReturns(new Placement(false, null, "EOrder:Insufficient funds"));

        await NewJob().ExecuteRuleAsync(rule.Id, CancellationToken.None);

        Assert.Empty(await Orders());
        Assert.Contains("Insufficient funds", (await ReloadRule(rule.Id)).LastResult);
    }

    // ── Resolving an unconfirmed buy ────────────────────────────────────────

    [Fact]
    public async Task Placing_FoundOnKrakenByUserRef_BecomesBuying()
    {
        var order = await SeedOrder("Placing", o => { o.BuyOrderId = null; o.BuyUserRef = 4242; });
        Gateway.Setup(g => g.FindOrderByUserRefAsync(4242)).ReturnsAsync((true, (CombinedOrder?)new CombinedOrder { Id = "B9" }));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var saved = Assert.Single(await Orders());
        Assert.Equal("Buying", saved.Status);
        Assert.Equal("B9", saved.BuyOrderId);
    }

    [Fact]
    public async Task Placing_ConfirmedAbsentForLongEnough_IsRemoved()
    {
        await SeedOrder("Placing", o => { o.BuyOrderId = null; o.BuyUserRef = 4242; o.CreatedAt = DateTime.UtcNow.AddMinutes(-10); });
        Gateway.Setup(g => g.FindOrderByUserRefAsync(4242)).ReturnsAsync((true, (CombinedOrder?)null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Empty(await Orders());
    }

    [Fact]
    public async Task Placing_WhenKrakenCantBeAsked_IsKeptNotGuessedAbout()
    {
        await SeedOrder("Placing", o => { o.BuyOrderId = null; o.BuyUserRef = 4242; o.CreatedAt = DateTime.UtcNow.AddMinutes(-10); });
        Gateway.Setup(g => g.FindOrderByUserRefAsync(4242)).ReturnsAsync((false, (CombinedOrder?)null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Placing", Assert.Single(await Orders()).Status);
    }

    // ── Buy fills → sell ────────────────────────────────────────────────────

    [Fact]
    public async Task FilledBuy_GetsASellAtTheRulesRise_ForWhatWasActuallyBought()
    {
        await SeedOrder("Buying");
        KrakenOrder("B1", OrderStatus.Closed, filled: 0.0015m); // a partial fill, then closed
        SellPlacementReturns(new Placement(true, "S1", null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.Equal("Selling", order.Status);
        Assert.Equal("S1", order.SellOrderId);
        Assert.Equal(54_945.0m, order.SellPrice);   // 49,950 * 1.10
        Assert.Equal(0.0015m, order.Quantity);
        Assert.NotNull(order.SellUserRef);
        Gateway.Verify(g => g.PlaceOrderWithUserRefAsync(Pair, OrderSide.Sell, OrderType.Limit, 0.0015m, 54_945.0m,
            It.IsAny<uint>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task BuyStillRestingInTheCache_IsLeftAlone()
    {
        await SeedOrder("Buying");
        State.Orders["B1"] = new OrderDto { Id = "B1", Status = "Open" };

        await NewJob().ExecuteAsync(CancellationToken.None);

        NoPlacements();
        Assert.Equal("Buying", Assert.Single(await Orders()).Status);
    }

    [Fact]
    public async Task BuyTooYoungForTheCacheToBeTrusted_IsLeftAlone()
    {
        await SeedOrder("Buying", o => o.CreatedAt = DateTime.UtcNow.AddSeconds(-20));

        await NewJob().ExecuteAsync(CancellationToken.None);

        NoPlacements();
        Gateway.Verify(g => g.GetOrderInfoAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BuyCancelledWithNothingFilled_IsNotSold()
    {
        await SeedOrder("Buying");
        KrakenOrder("B1", OrderStatus.Canceled, filled: 0m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        NoPlacements(); // absent from the cache is not the same as filled
        Assert.Equal("Cancelled", Assert.Single(await Orders()).Status);
    }

    [Fact]
    public async Task BuyThatCantBeVerified_IsRetriedLater()
    {
        await SeedOrder("Buying");
        Gateway.Setup(g => g.GetOrderInfoAsync("B1")).ReturnsAsync((CombinedOrder?)null);

        await NewJob().ExecuteAsync(CancellationToken.None);

        NoPlacements();
        Assert.Equal("Buying", Assert.Single(await Orders()).Status);
    }

    [Fact]
    public async Task SellOutcomeUnknown_KeepsTheOrderBuying_ForALaterLookup()
    {
        await SeedOrder("Buying");
        KrakenOrder("B1", OrderStatus.Closed, filled: 0.002m);
        SellPlacementReturns(new Placement(false, null, "Request timed out", Unknown: true));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.Equal("Buying", order.Status);
        Assert.NotNull(order.SellUserRef); // so the next tick can adopt the sell if Kraken did accept it
        Assert.Contains("unconfirmed", order.Note);
    }

    // ── Sell side ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FilledSell_MarksTheOrderSold()
    {
        await SeedOrder("Selling", o => { o.SellOrderId = "S1"; o.SellPrice = 54_945m; });
        KrakenOrder("S1", OrderStatus.Closed, filled: 0.002m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.Equal("Sold", order.Status);
        Assert.NotNull(order.SoldAt);
    }

    [Fact]
    public async Task SellCancelledUnfilled_IsFlaggedAsStillHeld()
    {
        await SeedOrder("Selling", o => o.SellOrderId = "S1");
        KrakenOrder("S1", OrderStatus.Canceled, filled: 0m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.Equal("Cancelled", order.Status);
        Assert.Contains("still held", order.Note);
    }

    [Fact]
    public async Task StopLoss_CancelsTheRestingSell_AndReSellsNearTheLowerMarket_NotPostOnly()
    {
        var r = Rule(); r.StopLossEnabled = true; r.StopLossPct = 95m; r.Active = false;
        var rule = await Seed(r);
        await SeedOrder("Selling", o => { o.BuyPrice = 100m; o.SellOrderId = "S1"; o.SellPrice = 110m; o.Quantity = 2m; }, rule);
        State.Orders["S1"] = new OrderDto { Id = "S1", Status = "Open" };
        SetMarket(price: 94m, change24h: -6m); // <= 95% of the 100 buy price
        Gateway.Setup(g => g.CancelOrderAsync("S1")).ReturnsAsync(true);
        SellPlacementReturns(new Placement(true, "S2", null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var order = Assert.Single(await Orders());
        Assert.True(order.StopLossTriggered);
        Assert.Equal("S2", order.SellOrderId);
        Assert.Equal(94.1m, order.SellPrice); // 94 * 1.001
        Gateway.Verify(g => g.PlaceOrderWithUserRefAsync(Pair, OrderSide.Sell, OrderType.Limit, 2m, 94.1m,
            It.IsAny<uint>(), It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task StopLoss_PriceStillAboveTheStop_DoesNothing()
    {
        var r = Rule(); r.StopLossEnabled = true; r.StopLossPct = 95m; r.Active = false;
        var rule = await Seed(r);
        await SeedOrder("Selling", o => { o.BuyPrice = 100m; o.SellOrderId = "S1"; }, rule);
        State.Orders["S1"] = new OrderDto { Id = "S1", Status = "Open" };
        SetMarket(price: 97m, change24h: -6m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
        Assert.False(Assert.Single(await Orders()).StopLossTriggered);
    }
}
