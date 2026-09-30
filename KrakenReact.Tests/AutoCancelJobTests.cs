using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class AutoCancelJobTests : OrderJobTestBase
{
    public AutoCancelJobTests()
    {
        State.AutoCancelEnabled = true;
        State.AutoCancelDays = 30;
        State.AutoCancelBuys = true;
        State.AutoCancelSells = true;
        Gateway.Setup(g => g.CancelOrderAsync(It.IsAny<string>())).ReturnsAsync(true);
    }

    private AutoCancelJob NewJob() => new(State, Gateway.Object, Notifier.Object, Factory, new Mock<ILogger<AutoCancelJob>>().Object);

    private void Order(string id, string side, int ageDays, string status = "Open") =>
        State.Orders[id] = new OrderDto { Id = id, Symbol = "XBTUSD", Side = side, Status = status, Price = 100m, Quantity = 1m, CreateTime = DateTime.UtcNow.AddDays(-ageDays) };

    [Fact]
    public async Task OldOpenOrder_IsCancelled()
    {
        Order("OLD", "Buy", 45);

        await NewJob().ExecuteAsync();

        Gateway.Verify(g => g.CancelOrderAsync("OLD"), Times.Once);
    }

    [Fact]
    public async Task RecentOrder_IsKept()
    {
        Order("NEW", "Buy", 5);
        await NewJob().ExecuteAsync();
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClosedOrder_IsNotCancelled()
    {
        Order("DONE", "Buy", 90, status: "Closed");
        await NewJob().ExecuteAsync();
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OnlyTheSidesEnabledAreCancelled()
    {
        State.AutoCancelSells = false;
        Order("B", "Buy", 45); Order("S", "Sell", 45);

        await NewJob().ExecuteAsync();

        Gateway.Verify(g => g.CancelOrderAsync("B"), Times.Once);
        Gateway.Verify(g => g.CancelOrderAsync("S"), Times.Never);
    }

    [Fact]
    public async Task ASellOwnedByAMicroTradePosition_IsLeftAlone()
    {
        Order("MT-SELL", "Sell", 60); Order("PLAIN", "Sell", 60);
        await Seed(new MicroTradeOrder { RuleId = 1, Symbol = "XBT/USD", Status = "Selling", SellOrderId = "MT-SELL" });

        await NewJob().ExecuteAsync();

        Gateway.Verify(g => g.CancelOrderAsync("MT-SELL"), Times.Never);   // stripping its exit would strand the position
        Gateway.Verify(g => g.CancelOrderAsync("PLAIN"), Times.Once);
    }

    [Fact]
    public async Task ABracketsOrders_AreLeftAlone()
    {
        Order("PARENT", "Buy", 60); Order("TP", "Sell", 60);
        await Seed(new BracketOrder { KrakenOrderId = "PARENT", TakeProfitOrderId = "TP", Symbol = "XBT/USD", Status = "Active" });

        await NewJob().ExecuteAsync();

        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AFinishedMicroTradeOrder_NoLongerProtectsItsOrderId()
    {
        Order("OLD-SELL", "Sell", 60);
        await Seed(new MicroTradeOrder { RuleId = 1, Symbol = "XBT/USD", Status = "Sold", SellOrderId = "OLD-SELL" });

        await NewJob().ExecuteAsync();

        Gateway.Verify(g => g.CancelOrderAsync("OLD-SELL"), Times.Once);
    }

    [Fact]
    public async Task DryRun_CancelsNothing()
    {
        State.DryRunJobs = true;
        Order("OLD", "Buy", 45);
        await NewJob().ExecuteAsync();
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        State.AutoCancelEnabled = false;
        Order("OLD", "Buy", 45);
        await NewJob().ExecuteAsync();
        Gateway.Verify(g => g.CancelOrderAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OneFailingCancel_DoesNotStopTheRest()
    {
        Order("A", "Buy", 45); Order("B", "Buy", 45);
        Gateway.Setup(g => g.CancelOrderAsync("A")).ThrowsAsync(new HttpRequestException("network"));

        await NewJob().ExecuteAsync();

        Gateway.Verify(g => g.CancelOrderAsync("B"), Times.Once);
    }
}
