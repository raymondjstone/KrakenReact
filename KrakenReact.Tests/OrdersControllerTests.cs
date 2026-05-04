using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using KrakenReact.Server.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class OrdersControllerTests
{
    private static (OrdersController controller, TradingStateService state) CreateController()
    {
        var delistedLogger = new Mock<ILogger<DelistedPriceService>>();
        var delisted = new DelistedPriceService(delistedLogger.Object);
        var state = new TradingStateService(delisted);
        var hub = new Mock<IHubContext<TradingHub>>();
        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);

        // GetAll() only uses _state — pass null for kraken since Create/Amend/Cancel need it but GetAll doesn't
        var dbFactory = new Mock<IDbContextFactory<KrakenDbContext>>();
        var controller = new OrdersController(state, null!, hub.Object, dbFactory.Object);
        return (controller, state);
    }

    [Fact]
    public void GetAll_EmptyOrders_ReturnsEmptyList()
    {
        var (controller, _) = CreateController();
        var result = controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var orders = Assert.IsType<List<OrderDto>>(ok.Value);
        Assert.Empty(orders);
    }

    [Fact]
    public void GetAll_WithOrders_ReturnsSortedByCreateTimeDesc()
    {
        var (controller, state) = CreateController();

        state.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Buy", Status = "Open",
            CreateTime = DateTime.UtcNow.AddHours(-2)
        });
        state.Orders.TryAdd("o2", new OrderDto
        {
            Id = "o2", Symbol = "ETHUSD", Side = "Sell", Status = "Open",
            CreateTime = DateTime.UtcNow.AddHours(-1)
        });
        state.Orders.TryAdd("o3", new OrderDto
        {
            Id = "o3", Symbol = "XBTUSD", Side = "Buy", Status = "Open",
            CreateTime = DateTime.UtcNow
        });

        var result = controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var orders = Assert.IsType<List<OrderDto>>(ok.Value);

        Assert.Equal(3, orders.Count);
        Assert.Equal("o3", orders[0].Id); // Most recent first
        Assert.Equal("o2", orders[1].Id);
        Assert.Equal("o1", orders[2].Id);
    }
}
