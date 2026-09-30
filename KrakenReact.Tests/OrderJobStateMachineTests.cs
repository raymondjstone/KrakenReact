using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

using Placement = KrakenRestService.PlacementResult;

/// <summary>Shared setup: an in-memory database, a fake exchange and a fake notifier.</summary>
public abstract class OrderJobTestBase
{
    protected readonly IDbContextFactory<KrakenDbContext> Factory = new InMemoryDbFactory($"jobs-{Guid.NewGuid()}");
    protected readonly Mock<IOrderGateway> Gateway = new();
    protected readonly Mock<INotifier> Notifier = new();
    protected readonly TradingStateService State =
        new(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));

    protected OrderJobTestBase()
    {
        Notifier.Setup(n => n.Pushover(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
    }

    protected async Task<T> Seed<T>(T entity) where T : class
    {
        await using var db = Factory.CreateDbContext();
        db.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }
}

public class ScheduledOrderJobTests : OrderJobTestBase
{
    private ScheduledOrderJob NewJob() => new(Factory, Gateway.Object, State, Notifier.Object,
        new Mock<ILogger<ScheduledOrderJob>>().Object, TestDiagnostics.Create());

    private static ScheduledOrder Due(string status = "Pending") => new()
    {
        Symbol = "XBT/USD", Side = "Buy", Price = 50000m, Quantity = 0.1m,
        ScheduledAt = DateTime.UtcNow.AddMinutes(-1), Status = status,
    };

    private async Task<ScheduledOrder> Reload(int id)
    {
        await using var db = Factory.CreateDbContext();
        return await db.ScheduledOrders.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private void GatewayReturns(Placement result) =>
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task DueOrder_IsPlaced_AndMarkedExecuted()
    {
        var order = await Seed(Due());
        GatewayReturns(new Placement(true, "OABC", null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var saved = await Reload(order.Id);
        Assert.Equal("Executed", saved.Status);
        Assert.NotNull(saved.ExecutedAt);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync("XBT/USD", OrderSide.Buy, OrderType.Limit, 0.1m, 50000m,
            It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task OrderNotYetDue_IsLeftAlone()
    {
        var order = await Seed(new ScheduledOrder { Symbol = "XBT/USD", Side = "Buy", Price = 1m, Quantity = 1m, ScheduledAt = DateTime.UtcNow.AddHours(1) });

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Pending", (await Reload(order.Id)).Status);
        Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DefiniteRejection_MarksFailedWithReason()
    {
        var order = await Seed(Due());
        GatewayReturns(new Placement(false, null, "EOrder:Insufficient funds"));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var saved = await Reload(order.Id);
        Assert.Equal("Failed", saved.Status);
        Assert.Equal("EOrder:Insufficient funds", saved.ErrorMessage);
        Assert.Null(saved.ExecutedAt);
    }

    [Fact]
    public async Task UnknownOutcome_BecomesUnconfirmed_AndIsNeverPlacedAgain()
    {
        var order = await Seed(Due());
        GatewayReturns(new Placement(false, null, "Request timed out", Unknown: true));

        var job = NewJob();
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None); // the next minute's tick

        Assert.Equal("Unconfirmed", (await Reload(order.Id)).Status);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task GatewayThrowing_IsUnconfirmed_NotRetried()
    {
        var order = await Seed(Due());
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        var job = NewJob();
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        // The exception may have come after Kraken accepted the order, so it must not be treated as a clean failure
        Assert.Equal("Unconfirmed", (await Reload(order.Id)).Status);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task OrphanedPlacing_BecomesUnconfirmed_WithoutResending()
    {
        // A row left in "Placing" by a crash: the order may exist on Kraken, so it is flagged, never re-sent
        var stale = Due("Placing");
        stale.ExecutedAt = DateTime.UtcNow.AddMinutes(-10);
        var order = await Seed(stale);

        await NewJob().ExecuteAsync(CancellationToken.None);

        var saved = await Reload(order.Id);
        Assert.Equal("Unconfirmed", saved.Status);
        Assert.Contains("interrupted", saved.ErrorMessage);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RecentPlacing_IsLeftAloneForNow()
    {
        var recent = Due("Placing");
        recent.ExecutedAt = DateTime.UtcNow.AddSeconds(-20);
        var order = await Seed(recent);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Placing", (await Reload(order.Id)).Status);
        Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DryRun_MarksExecutedWithoutTouchingTheExchange()
    {
        State.DryRunJobs = true;
        var order = await Seed(Due());

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Executed", (await Reload(order.Id)).Status);
        Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EachOrderIsRecordedIndependently()
    {
        var first = await Seed(Due());
        var second = await Seed(Due());
        Gateway.SetupSequence(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new Placement(true, "O1", null))
            .ReturnsAsync(new Placement(false, null, "EOrder:Invalid price"));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var statuses = new[] { (await Reload(first.Id)).Status, (await Reload(second.Id)).Status }.OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Executed", "Failed" }, statuses);
    }
}

public class BracketMonitorJobTests : OrderJobTestBase
{
    private BracketMonitorJob NewJob() => new(Factory, Gateway.Object, State, Notifier.Object,
        new Mock<ILogger<BracketMonitorJob>>().Object, TestDiagnostics.Create());

    private async Task<BracketOrder> Reload(int id)
    {
        await using var db = Factory.CreateDbContext();
        return await db.BracketOrders.AsNoTracking().SingleAsync(b => b.Id == id);
    }

    private static BracketOrder Watching() => new()
    {
        KrakenOrderId = "PARENT", Symbol = "XBT/USD", Side = "Buy", Quantity = 1m,
        EntryPrice = 100m, StopPrice = 90m, TakeProfitPrice = 120m,
        Status = "Watching", CreatedAt = DateTime.UtcNow.AddMinutes(-5),
    };

    private static BracketOrder Active() => new()
    {
        KrakenOrderId = "PARENT", Symbol = "XBT/USD", Side = "Buy", Quantity = 1m,
        EntryPrice = 100m, StopPrice = 90m, TakeProfitPrice = 120m, TakeProfitOrderId = "TP1",
        Status = "Active", CreatedAt = DateTime.UtcNow.AddMinutes(-10), ActivatedAt = DateTime.UtcNow.AddMinutes(-5),
    };

    private void KrakenOrder(string id, OrderStatus status, decimal filled) =>
        Gateway.Setup(g => g.GetOrderInfoAsync(id)).ReturnsAsync(new CombinedOrder { Id = id, Status = status, QuantityFilled = filled, Quantity = 1m });

    private void PriceIs(decimal price)
    {
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        item.TickerData = new TickerDataItem { LastTradePrice = price };
        State.Prices["XBT/USD"] = item;
    }

    private void PlacementReturns(OrderType type, Placement result) =>
        Gateway.Setup(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), type,
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(result);

    // ── Watching ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Watching_TooYoung_DoesNothing()
    {
        var b = Watching();
        b.CreatedAt = DateTime.UtcNow.AddSeconds(-5);
        var saved = await Seed(b);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Watching", (await Reload(saved.Id)).Status);
        Gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Watching_ParentStillOpen_KeepsWaiting()
    {
        var saved = await Seed(Watching());
        KrakenOrder("PARENT", OrderStatus.Open, 0m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Watching", (await Reload(saved.Id)).Status);
    }

    [Fact]
    public async Task Watching_ParentCancelledUnfilled_IsCancelled_AndNoExitsAreSent()
    {
        var saved = await Seed(Watching());
        KrakenOrder("PARENT", OrderStatus.Canceled, 0m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Cancelled", b.Status);
        Assert.Contains("without filling", b.Note);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Watching_ParentCantBeVerified_TriesAgainLater()
    {
        var saved = await Seed(Watching());
        Gateway.Setup(g => g.GetOrderInfoAsync("PARENT")).ReturnsAsync((CombinedOrder?)null);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Watching", (await Reload(saved.Id)).Status);
    }

    [Fact]
    public async Task Watching_ParentFilled_PlacesTakeProfitForTheFilledQuantity()
    {
        var saved = await Seed(Watching());
        KrakenOrder("PARENT", OrderStatus.Closed, 0.6m); // partial fill of a 1.0 order
        PlacementReturns(OrderType.Limit, new Placement(true, "TP1", null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Active", b.Status);
        Assert.Equal("TP1", b.TakeProfitOrderId);
        Assert.Equal(0.6m, b.Quantity);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), OrderSide.Sell, OrderType.Limit, 0.6m, 120m,
            It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Watching_TakeProfitRejected_CancelsTheBracket()
    {
        var saved = await Seed(Watching());
        KrakenOrder("PARENT", OrderStatus.Closed, 1m);
        PlacementReturns(OrderType.Limit, new Placement(false, null, "EOrder:Insufficient funds"));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Cancelled", b.Status);
        Assert.Contains("Insufficient funds", b.Note);
    }

    // ── Active ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Active_TakeProfitFilled_IsTookProfit()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Closed, 1m);
        PriceIs(125m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("TookProfit", (await Reload(saved.Id)).Status);
    }

    [Fact]
    public async Task Active_PriceAboveStop_TakeProfitResting_NothingHappens()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Open, 0m);
        PriceIs(105m);

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Active", (await Reload(saved.Id)).Status);
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Active_StopHit_CancelsTakeProfit_ThenExitsAtMarket()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Open, 0m);
        PriceIs(89m);
        Gateway.Setup(g => g.CancelOrderAsync("TP1")).ReturnsAsync(true);
        PlacementReturns(OrderType.Market, new Placement(true, "EXIT1", null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Stopped", b.Status);
        Assert.Null(b.TakeProfitOrderId);
        Gateway.Verify(g => g.CancelOrderAsync("TP1"), Times.Once);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), OrderSide.Sell, OrderType.Market, 1m, 0m,
            It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task Active_StopHit_ButCancelFails_DoesNotSellCoinsThatAreStillLocked()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Open, 0m);
        PriceIs(85m);
        Gateway.Setup(g => g.CancelOrderAsync("TP1")).ReturnsAsync(false);

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Active", b.Status);
        Assert.Equal("TP1", b.TakeProfitOrderId);
        Gateway.Verify(g => g.PlaceOrderWithRecoveryAsync(It.IsAny<string>(), It.IsAny<OrderSide>(), It.IsAny<OrderType>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Active_StopHit_ExitRejected_StaysActiveToRetry()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Open, 0m);
        PriceIs(88m);
        Gateway.Setup(g => g.CancelOrderAsync("TP1")).ReturnsAsync(true);
        PlacementReturns(OrderType.Market, new Placement(false, null, "EGeneral:Temporary lockout"));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Active", b.Status);
        Assert.Null(b.TakeProfitOrderId); // already cancelled, so the next tick goes straight to the exit
        Assert.Contains("retrying", b.Note);
    }

    [Fact]
    public async Task Active_StopHit_ExitUnconfirmed_StopsTrackingRatherThanSellingTwice()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Open, 0m);
        PriceIs(88m);
        Gateway.Setup(g => g.CancelOrderAsync("TP1")).ReturnsAsync(true);
        PlacementReturns(OrderType.Market, new Placement(false, null, "Request timed out", Unknown: true));

        await NewJob().ExecuteAsync(CancellationToken.None);

        var b = await Reload(saved.Id);
        Assert.Equal("Stopped", b.Status);
        Assert.Contains("could not be confirmed", b.Note);
    }

    [Fact]
    public async Task Active_TakeProfitCancelledByHand_StopStillProtects()
    {
        var saved = await Seed(Active());
        KrakenOrder("TP1", OrderStatus.Canceled, 0m);
        PriceIs(80m);
        PlacementReturns(OrderType.Market, new Placement(true, "EXIT1", null));

        await NewJob().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Stopped", (await Reload(saved.Id)).Status);
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never); // nothing left to cancel
    }
}
