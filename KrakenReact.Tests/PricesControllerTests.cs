using KrakenReact.Server.Controllers;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

/// <summary>
/// Tests for PricesController.GetAll() which is purely in-memory and doesn't need KrakenRestService or DbMethods.
/// The klines endpoint requires real service dependencies so is tested at integration level.
/// </summary>
public class PricesControllerTests
{
    private static (PricesController controller, TradingStateService state) CreateController()
    {
        var delistedLogger = new Mock<ILogger<DelistedPriceService>>();
        var delisted = new DelistedPriceService(delistedLogger.Object);
        var state = new TradingStateService(delisted);
        var logger = new Mock<ILogger<PricesController>>();

        // PricesController.GetAll() only uses _state — pass nulls for kraken/db since they're only used by klines endpoint
        var controller = new PricesController(state, null!, null!, logger.Object);
        return (controller, state);
    }

    [Fact]
    public void GetAll_EmptyState_ReturnsEmptyList()
    {
        var (controller, _) = CreateController();
        var result = controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prices = Assert.IsType<List<PriceDto>>(ok.Value);
        Assert.Empty(prices);
    }

    [Fact]
    public void GetAll_WithPrices_ReturnsPriceDtos()
    {
        var (controller, state) = CreateController();

        var priceItem = new PriceDataItem { Symbol = "SOL/USD", SupportedPair = true, KrakenNewPricesLoadedEver = true };
        priceItem.AddKline(new DerivedKline
        {
            Asset = "SOL/USD",
            OpenTime = DateTime.UtcNow,
            Open = 98m, High = 105m, Low = 95m, Close = 100m, Volume = 1000m
        });
        state.Prices.TryAdd("SOL/USD", priceItem);

        var result = controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prices = Assert.IsType<List<PriceDto>>(ok.Value);

        Assert.Single(prices);
        Assert.Equal("SOL/USD", prices[0].Symbol);
        Assert.Equal("SOL/USD", prices[0].DisplaySymbol);
        Assert.Equal("SOL", prices[0].Base);
        Assert.Equal("USD", prices[0].CCY);
        Assert.Equal(100m, prices[0].ClosePrice);
    }

    [Fact]
    public void GetAll_NormalizesDisplaySymbol()
    {
        var (controller, state) = CreateController();

        var priceItem = new PriceDataItem { Symbol = "XBT/USD" };
        priceItem.AddKline(new DerivedKline { Asset = "XBT/USD", Close = 50000m, OpenTime = DateTime.UtcNow });
        state.Prices.TryAdd("XBT/USD", priceItem);

        var result = controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prices = Assert.IsType<List<PriceDto>>(ok.Value);

        // XBT should normalize to BTC in DisplaySymbol
        Assert.Equal("BTC/USD", prices[0].DisplaySymbol);
        Assert.Equal("BTC", prices[0].Base);
    }

    [Fact]
    public void GetAll_CalculatesAverageBuyPrice()
    {
        var (controller, state) = CreateController();

        var priceItem = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = true };
        priceItem.AddKline(new DerivedKline { Asset = "SOL/USD", Close = 100m, OpenTime = DateTime.UtcNow });
        state.Prices.TryAdd("SOL/USD", priceItem);

        // Add a closed buy order
        state.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Buy", Status = "Closed",
            AveragePrice = 80m
        });

        var result = controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prices = Assert.IsType<List<PriceDto>>(ok.Value);

        Assert.Equal(80m, prices[0].AverageBuyPrice);
        Assert.False(prices[0].PriceLowerThanBuy); // 100 > 80
    }

    [Fact]
    public void GetAll_DetectsPriceLowerThanBuy()
    {
        var (controller, state) = CreateController();

        var priceItem = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = true };
        priceItem.AddKline(new DerivedKline { Asset = "SOL/USD", Close = 70m, OpenTime = DateTime.UtcNow });
        state.Prices.TryAdd("SOL/USD", priceItem);

        state.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Buy", Status = "Closed",
            AveragePrice = 80m
        });

        var result = controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var prices = Assert.IsType<List<PriceDto>>(ok.Value);

        Assert.True(prices[0].PriceLowerThanBuy);
    }
}
